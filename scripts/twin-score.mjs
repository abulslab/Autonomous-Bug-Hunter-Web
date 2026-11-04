#!/usr/bin/env node
// Scores a project's app twin against the app's answer key (ground-truth/<app>/answer-key.json): how much of the real
// API and pages the twin knows (recall), how much of what it knows is real (precision), and which roles it has seen.
// Appends a row to artifacts/twin-results.jsonl; `node scripts/benchmark.mjs --report` puts the rows in BENCHMARKS.md.
//
//   node scripts/twin-score.mjs --app hotel [--label "no AI, guest + front desk"]

import { appendFileSync, mkdirSync, readFileSync } from "node:fs";

const args = process.argv.slice(2);
const opt = (n) => (args.includes(`--${n}`) ? args[args.indexOf(`--${n}`) + 1] : null);
const app = opt("app");
if (!["shop", "hotel"].includes(app)) throw new Error("Usage: node scripts/twin-score.mjs --app shop|hotel [--label text]");
const API = (process.env.API ?? "http://localhost:5280").replace(/\/$/, "") + "/api";
const headers = process.env.BUGHUNTER_API_KEY ? { "x-api-key": process.env.BUGHUNTER_API_KEY } : {};

const key = JSON.parse(readFileSync(new URL(`../ground-truth/${app}/answer-key.json`, import.meta.url), "utf8"));
const res = await fetch(`${API}/projects/${app}/twin`, { headers });
if (!res.ok) throw new Error(`No twin for project ${app} (${res.status}). Run a hunt from the project first.`);
const twin = await res.json();

// Pages: "#/book/{roomId}" and the twin's "/#/book/{id}" are the same page; queries and sign-in markers do not matter.
const page = (p) => {
  const s = p.replace(/ \[signed in\]$/, "").replace(/^\//, "").split("?")[0].replace(/\{[^}]+\}/g, "{id}");
  return s === "" || s === "#" ? "#/" : s;
};
const endpoint = (m, p) => `${m.toUpperCase()} ${p.replace(/\{[^}]+\}/g, "{id}")}`;

const realEndpoints = new Set(key.endpoints.map((e) => endpoint(e.method, e.path)));
const twinEndpoints = new Set(twin.endpoints.map((e) => endpoint(e.method, e.path)));
const realPages = new Set(key.features.filter((f) => !f.feature).flatMap((f) => f.pages).map(page));
const twinPages = new Set(twin.states.map((s) => page(s.id)));

const hit = [...realEndpoints].filter((e) => twinEndpoints.has(e));
const extra = [...twinEndpoints].filter((e) => !realEndpoints.has(e));
const pagesHit = [...realPages].filter((p) => twinPages.has(p));
const roles = Object.fromEntries(key.roles.map((r) => [r.id, twin.endpoints.filter((e) => e.roles.includes(r.id)).length]));

// Permissions: for every answer-key endpoint and project role the twin tried, does what it saw match what should be?
// A mismatch with defects on is a permission defect found; with defects off it is a twin error.
const perms = await (await fetch(`${API}/projects/${app}/permissions`, { headers })).json();
const byEndpoint = Object.fromEntries(perms.map((r) => [r.endpoint.replace(/\{[^}]+\}/g, "{id}"), r.roles]));
const permission = { checked: 0, agree: 0, mismatches: [] };
for (const e of key.endpoints) {
  const seen = byEndpoint[endpoint(e.method, e.path)];
  if (!seen) continue;
  for (const role of key.roles.map((r) => r.id)) {
    const got = seen[role];
    if (!got || got === "unknown" || got === "error") continue;
    const expected = e.roles.includes(role) ? "allowed" : "denied";
    permission.checked++;
    if (got === expected) permission.agree++;
    else permission.mismatches.push(`${endpoint(e.method, e.path)} as ${role}: ${got}, expected ${expected}`);
  }
}

// Understanding (P4): what a model proposed and the verifier kept, matched to the answer key. An entity matches by
// name (singular, case-insensitive); a feature by a shared endpoint or page; a lifecycle by entity and a shared
// transition endpoint; a rule by its endpoint and a shared field.
const norm = (n) => n.toLowerCase().replace(/[^a-z]/g, "").replace(/s$/, "");
const fields = (check) => (check.replace(/'[^']*'/g, " ").match(/[A-Za-z_][A-Za-z0-9_.\[\]]*/g) ?? []).flatMap((x) => x.split(".")).map((x) => x.replace("[]", "").toLowerCase())
  .filter((x) => x && !["status", "request", "response", "prev", "query", "and", "or", "not", "true", "false", "null", "sum", "count", "all", "any", "min", "max", "abs", "round", "len", "lower", "contains", "exists", "date", "today", "items", "item"].includes(x));
const match = (real, mine, same) => {
  const found = real.filter((r) => mine.some((m) => same(r, m)));
  const correct = mine.filter((m) => real.some((r) => same(r, m)));
  return { real: real.length, found: found.length, proposed: mine.length, correct: correct.length, missing: real.filter((r) => !mine.some((m) => same(r, m))) };
};
const keyEp = (x) => endpoint(...x.split(" "));
const understanding = twin.understanding ? {
  model: twin.understanding.model, seconds: twin.understanding.seconds, costUsd: twin.understanding.costUsd, proposed: twin.understanding.proposed, admitted: twin.understanding.admitted,
  rejected: twin.rejected.length,
  entities: match(key.entities.map((e) => e.name), twin.entities.map((e) => e.name), (r, m) => norm(r) === norm(m) || norm(m).includes(norm(r)) || norm(r).includes(norm(m))),
  features: match(key.features.filter((f) => !f.feature), twin.features,
    (r, m) => m.endpoints.some((e) => r.endpoints.map((x) => keyEp(x)).includes(keyEp(e))) || m.pages.some((pg) => r.pages.map(page).includes(page(pg)))),
  lifecycles: match(key.lifecycles, twin.lifecycles, (r, m) => norm(r.entity) === norm(m.entity) && m.transitions.some((t) => r.transitions.some((x) => keyEp(x.endpoint) === keyEp(t.endpoint)))),
  rules: match(key.rules.filter((r) => r.check && !r.since), twin.rules,
    (r, m) => keyEp(r.check.on) === keyEp(m.on) && fields(m.check).some((f) => fields(r.check.check).includes(f))),
} : null;
if (understanding) {
  for (const k of ["entities", "features", "lifecycles", "rules"]) delete understanding[k].missing;
}

// Journeys (P5): answer-key journeys (outside optional features) proven by a journey of the same role and outcome.
const lib = await (await fetch(`${API}/projects/${app}/journeys`, { headers })).json();
const cov = await (await fetch(`${API}/projects/${app}/coverage`, { headers })).json();
const outcomeOf = (text) => { const m = text.match(/^(GET|POST|PUT|PATCH|DELETE) (\S+)/); return m ? endpoint(m[1], m[2]) : null; };
const realJourneys = key.journeys.filter((j) => !j.feature).map((j) => ({ id: j.id, role: j.role, outcome: outcomeOf(j.outcome) }));
const provenJ = lib.journeys.filter((j) => j.status === "proven");
const journeys = {
  real: realJourneys.length,
  proven: realJourneys.filter((r) => provenJ.some((j) => j.role === r.role && endpoint(...j.outcome.split(" ")) === r.outcome)).length,
  missing: realJourneys.filter((r) => !provenJ.some((j) => j.role === r.role && endpoint(...j.outcome.split(" ")) === r.outcome)).map((r) => `${r.id} (${r.role})`),
  library: lib.journeys.length, provenInLibrary: provenJ.length, flaky: lib.journeys.filter((j) => j.status === "flaky").length,
  failing: lib.journeys.filter((j) => j.status === "failing").length, planned: lib.planned.length,
  storiesCovered: cov.covered, stories: cov.total,
};

const row = {
  at: new Date().toISOString(), app, label: opt("label") ?? "", twinVersion: twin.version, hunts: twin.version,
  endpoints: { real: realEndpoints.size, found: hit.length, extra: extra.length, missing: [...realEndpoints].filter((e) => !twinEndpoints.has(e)), extraList: extra },
  pages: { real: realPages.size, found: pagesHit.length, missing: [...realPages].filter((p) => !twinPages.has(p)) },
  endpointsByRole: roles,
  declared: twin.endpoints.filter((e) => e.declared).length,
  permission,
  understanding,
  journeys,
};
const pct = (a, b) => (b ? `${Math.round((100 * a) / b)}%` : "-");
console.log(`${app} twin v${twin.version}`);
console.log(`  endpoints: ${hit.length}/${realEndpoints.size} found (recall ${pct(hit.length, realEndpoints.size)}), ${extra.length} not in the answer key (precision ${pct(hit.length, twinEndpoints.size)})`);
if (row.endpoints.missing.length) console.log(`    missing: ${row.endpoints.missing.join(", ")}`);
if (extra.length) console.log(`    extra:   ${extra.join(", ")}`);
console.log(`  pages:     ${pagesHit.length}/${realPages.size} found (recall ${pct(pagesHit.length, realPages.size)})${row.pages.missing.length ? `; missing ${row.pages.missing.join(", ")}` : ""}`);
console.log(`  endpoints seen per role: ${Object.entries(roles).map(([r, n]) => `${r} ${n}`).join(", ")}`);
console.log(`  declared by a specification: ${row.declared}`);
console.log(`  permissions: ${permission.agree}/${permission.checked} role checks as the answer key says${permission.mismatches.length ? "; differences:" : ""}`);
for (const m of permission.mismatches) console.log(`    ${m}`);
console.log(`  journeys: ${journeys.proven}/${journeys.real} answer-key journeys proven; library ${journeys.library} (${journeys.provenInLibrary} proven, ${journeys.flaky} flaky, ${journeys.failing} failing), ${journeys.planned} planned; stories covered ${journeys.storiesCovered}/${journeys.stories}`);
if (journeys.missing.length) console.log(`    not proven yet: ${journeys.missing.join(", ")}`);
if (understanding) {
  console.log(`  understanding by ${understanding.model}: ${understanding.proposed} proposed, ${understanding.admitted} admitted, ${understanding.rejected} refused; ${understanding.seconds}s, $${understanding.costUsd}`);
  for (const k of ["entities", "features", "lifecycles", "rules"]) {
    const m = understanding[k];
    console.log(`    ${k.padEnd(10)} recall ${m.found}/${m.real} (${pct(m.found, m.real)}), precision ${m.correct}/${m.proposed} (${pct(m.correct, m.proposed)})`);
  }
}
mkdirSync("artifacts", { recursive: true });
appendFileSync("artifacts/twin-results.jsonl", JSON.stringify(row) + "\n");
