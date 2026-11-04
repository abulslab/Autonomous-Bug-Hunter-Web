#!/usr/bin/env node
// Benchmark harness: runs hunts against the demo apps for each configuration, with every seeded defect on (recall) and
// every defect off (false positives), and writes docs/BENCHMARKS.md from the results. Every number in the README
// comes from this file.
//
//   node scripts/benchmark.mjs                       # no-AI rows on the demo shop
//   node scripts/benchmark.mjs --model provider:model [--planner provider:model]   # plus the AI rows, with your model
//   node scripts/benchmark.mjs --app hotel           # the same matrix on Demo Hotel
//   node scripts/benchmark.mjs --only heuristic-80   # one configuration
//   node scripts/benchmark.mjs --defects all-on      # one defect set
//   node scripts/benchmark.mjs --report              # rewrite BENCHMARKS.md from saved results only
//
// Results are appended to artifacts/benchmark-results.jsonl, so runs accumulate and the report shows all of them.

import { appendFileSync, existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";

const API = (process.env.API ?? "http://localhost:5280").replace(/\/$/, "") + "/api/hunter";
const headers = { "content-type": "application/json", ...(process.env.BUGHUNTER_API_KEY ? { "x-api-key": process.env.BUGHUNTER_API_KEY } : {}) };
const RESULTS = "artifacts/benchmark-results.jsonl";

// AI rows run only when a model is named (--model), so a plain run never calls a provider by accident. The report names
// configurations by strategy, not by model: results depend on the model each team brings, and the point of the table
// is what the engine proves with it, not a model's brand.
const args0 = process.argv.slice(2);
const MODEL = args0.includes("--model") ? args0[args0.indexOf("--model") + 1] : null;
const PLANNER = args0.includes("--planner") ? args0[args0.indexOf("--planner") + 1] : MODEL;
const MATRIX = [
  { id: "heuristic-80", strategy: "Heuristic", steps: 80, label: "No AI (sweep, probes, oracles)" },
  // A project survey: one no-AI hunt per role, with the project's twin predictions and knowledge, combined into one row.
  { id: "survey-120", project: true, steps: 120, label: "No AI, every role (project survey, twin predictions)" },
  { id: "guided-60-ai", ai: true, strategy: "Guided", steps: 60, label: "Guided (AI plans intents and rules; the engine verifies)" },
  { id: "hybrid-60-ai", ai: true, strategy: "Hybrid", steps: 60, label: "Hybrid (no-AI sweep first, then AI)" },
];
const DEFECT_SETS = [{ id: "all-on", on: true }, { id: "all-off", on: false }];

// What each demo app is told: the same mission, stories and secrets in every run of that app.
const APPS = {
  shop: {
    name: "Demo shop", mission: "Find functional bugs in browsing, search, cart, coupons and checkout.",
    requirements: ["DEMO-121", "DEMO-122", "DEMO-123", "DEMO-124", "DEMO-125", "DEMO-126", "DEMO-127", "DEMO-128"],
    secrets: { email: "shopper01@shop.demo", password: "Shop123!" },
  },
  hotel: {
    name: "Demo Hotel", mission: "Find functional bugs in searching rooms, booking a stay and cancelling bookings.",
    requirements: ["HOTEL-1", "HOTEL-2", "HOTEL-3", "HOTEL-4", "HOTEL-5", "HOTEL-6"], secrets: {},
  },
};

async function call(path, body, method) {
  const res = await fetch(API + path, { method: method ?? (body ? "POST" : "GET"), headers, body: body ? JSON.stringify(body) : undefined });
  const text = await res.text();
  if (!res.ok) throw new Error(`${path} -> ${res.status}: ${text.slice(0, 300)}`);
  return text ? JSON.parse(text) : null;
}

async function runProject(app, cfg, defects) {
  const status = await call("/status");
  const demo = (status.demos ?? []).find((d) => d.app === app);
  if (!demo?.enabled) throw new Error(`${APPS[app].name} is not reachable from the API.`);
  await call(`/demo/defects?app=${app}`, Object.fromEntries(demo.defects.map((d) => [d.id, defects.on && !d.requires])), "PUT");
  const root = API.replace(/\/hunter$/, "");
  const res = await fetch(`${root}/projects/${app}/survey`, { method: "POST", headers, body: JSON.stringify({ maxSteps: cfg.steps, confirm: true }) });
  const survey = await res.json();
  if (!res.ok) throw new Error(`survey -> ${res.status}: ${JSON.stringify(survey).slice(0, 300)}`);
  process.stdout.write(`${app} ${cfg.id} / ${defects.id}: hunts ${survey.hunts.map((x) => x.hunt).join(", ")} `);
  const hunts = [];
  for (const x of survey.hunts) {
    for (;;) {
      const d = await call(`/hunts/${x.hunt}`);
      if (!d.active && !["Queued", "Running", "Confirming"].includes(d.hunt.status)) { hunts.push(d.hunt); break; }
      process.stdout.write(".");
      await new Promise((r) => setTimeout(r, 15000));
    }
  }
  // A seeded defect counts once, found by any role; confirmed if its credited finding reproduced in that hunt.
  const seeded = new Map(); const confirmedIds = new Set(); const via = {};
  for (const h of hunts) for (const sd of h.benchmark?.seeded ?? []) {
    if (!seeded.has(sd.id)) seeded.set(sd.id, false);
    if (!sd.findingId) continue;
    seeded.set(sd.id, true); via[sd.id] ??= sd.via;
    if (h.findings.find((f) => f.id === sd.findingId)?.reproduced === "Confirmed") confirmedIds.add(sd.id);
  }
  const sum = (f) => hunts.reduce((n, h) => n + (h.benchmark?.[f] ?? 0), 0);
  const row = {
    at: new Date().toISOString(), app, config: cfg.id, label: cfg.label, defects: defects.id, hunt: hunts.map((h) => h.id).join(" "), status: hunts.every((h) => h.status === "Completed") ? "Completed" : "Failed",
    steps: cfg.steps, seeded: seeded.size, detected: [...seeded.values()].filter(Boolean).length, confirmedDetected: confirmedIds.size,
    unexplained: sum("unmatchedObserved"), unexplainedRules: sum("unmatchedInvariant"), unexplainedRulesFlagged: sum("unmatchedInvariantFlagged"), unexplainedPredictions: sum("unmatchedPrediction"),
    suspicions: hunts.reduce((n, h) => n + h.findings.filter((f) => f.provenance === "Judged").length, 0),
    findings: hunts.reduce((n, h) => n + h.findings.length, 0), confirmed: hunts.reduce((n, h) => n + h.findings.filter((f) => f.reproduced === "Confirmed").length, 0),
    intents: 0, intentsHeld: 0, modelCalls: hunts.reduce((n, h) => n + h.usage.modelCalls, 0), costUsd: Number(hunts.reduce((n, h) => n + h.usage.costUsd, 0).toFixed(4)),
    minutes: Number(hunts.reduce((n, h) => n + (new Date(h.finishedAt) - new Date(h.startedAt)) / 60000, 0).toFixed(1)),
    found: [...seeded.entries()].filter(([, v]) => v).map(([k]) => `${k}${via[k] ? ` (${via[k]})` : ""}`),
  };
  console.log(` ${row.status}: ${row.detected}/${row.seeded} seeded, ${row.confirmedDetected} confirmed (${row.found.join(", ") || "none"}); unexplained ${row.unexplained} oracle, ${row.unexplainedRules} rule, ${row.unexplainedPredictions} prediction; ${row.modelCalls} model calls`);
  mkdirSync("artifacts", { recursive: true });
  appendFileSync(RESULTS, JSON.stringify(row) + "\n");
  return row;
}

async function run(app, cfg, defects) {
  if (cfg.project) return runProject(app, cfg, defects);
  const target = APPS[app];
  const status = await call("/status");
  const demo = (status.demos ?? []).find((d) => d.app === app);
  if (!demo?.enabled) throw new Error(`${target.name} is not reachable from the API.`);
  // "Every defect on" means every defect that can show: one inside a feature (requires) stays off while the feature is off.
  await call(`/demo/defects?app=${app}`, Object.fromEntries(demo.defects.map((d) => [d.id, defects.on && !d.requires])), "PUT");
  const { id } = await call("/hunts", {
    url: demo.url, strategy: cfg.strategy, maxSteps: cfg.steps, connection: cfg.ai ? MODEL : null, plannerConnection: cfg.ai ? PLANNER : null,
    maxCostUsd: 1.0, probe: true, confirm: true, shrink: false,
    mission: target.mission, requirements: target.requirements,
    secrets: target.secrets, // the server fills in the demo app's reset key for the "reset-key" setup secret
    setupUrl: demo.resetUrl, setupSecret: "reset-key",
  });
  process.stdout.write(`${app} ${cfg.id} / ${defects.id}: hunt ${id} `);
  let h;
  for (;;) {
    const d = await call(`/hunts/${id}`);
    h = d.hunt;
    if (!d.active && !["Queued", "Running", "Confirming"].includes(h.status)) break;
    process.stdout.write(".");
    await new Promise((r) => setTimeout(r, 15000));
  }
  const b = h.benchmark ?? { total: 0, detected: 0, confirmedDetected: 0, unmatchedObserved: 0, seeded: [] };
  const confirmed = h.findings.filter((f) => f.reproduced === "Confirmed").length;
  const row = {
    at: new Date().toISOString(), app, config: cfg.id, label: cfg.label, defects: defects.id, hunt: h.id, status: h.status,
    steps: cfg.steps, seeded: b.total, detected: b.detected, confirmedDetected: b.confirmedDetected ?? 0, unexplained: b.unmatchedObserved,
    unexplainedRules: b.unmatchedInvariant ?? 0, unexplainedRulesFlagged: b.unmatchedInvariantFlagged ?? 0,
    suspicions: h.findings.filter((f) => f.provenance === "Judged").length,
    findings: h.findings.length, confirmed, intents: h.intents?.length ?? 0, intentsHeld: (h.intents ?? []).filter((i) => i.status === "Held").length,
    modelCalls: h.usage.modelCalls, costUsd: Number(h.usage.costUsd.toFixed(4)), minutes: Number(((new Date(h.finishedAt) - new Date(h.startedAt)) / 60000).toFixed(1)),
    found: b.seeded.filter((s) => s.findingId).map((s) => s.id),
  };
  console.log(` ${row.status}: ${row.detected}/${row.seeded} seeded, ${row.confirmedDetected} confirmed, ${row.unexplained} unexplained (oracle), ${row.unexplainedRules} unexplained (rule, ${row.unexplainedRulesFlagged} flagged), ${row.modelCalls} model calls`);
  mkdirSync("artifacts", { recursive: true });
  appendFileSync(RESULTS, JSON.stringify(row) + "\n");
  return row;
}

// Twin accuracy rows from scripts/twin-score.mjs: what the app twin knows against each app's answer key.
function twinTables() {
  if (!existsSync("artifacts/twin-results.jsonl")) return [];
  const rows = readFileSync("artifacts/twin-results.jsonl", "utf8").trim().split("\n").filter(Boolean).map((l) => JSON.parse(l));
  const pct = (a, b) => (b ? `${Math.round((100 * a) / b)}%` : "-");
  return [
    "## App twin accuracy",
    "",
    "What a project's app twin knows after its hunts, scored against the app's answer key (`ground-truth/<app>/answer-key.json`).",
    "Recall: share of the real endpoints or pages the twin found. Precision: share of the twin's endpoints that are real.",
    "",
    "Permissions: role checks (endpoint x role tried) whose outcome matches the answer key; with seeded permission defects on, the differences are those defects.",
    "",
    "| App | Twin built from | Twin version | Endpoints found | Endpoint recall | Endpoint precision | Declared by spec | Pages found | Page recall | Permissions as specified | Permission differences |",
    "|---|---|---|---|---|---|---|---|---|---|---|",
    ...rows.map((r) => `| ${r.app} | ${r.label || "-"} | v${r.twinVersion} | ${r.endpoints.found} / ${r.endpoints.real} | ${pct(r.endpoints.found, r.endpoints.real)} | ${pct(r.endpoints.found, r.endpoints.found + r.endpoints.extra)} | ${r.declared ?? "-"} | ${r.pages.found} / ${r.pages.real} | ${pct(r.pages.found, r.pages.real)} | ${r.permission ? `${r.permission.agree} / ${r.permission.checked}` : "-"} | ${r.permission?.mismatches.join("; ") || "-"} |`),
    "",
    ...understandingTable(rows),
    ...journeyTable(rows),
  ];
}

// Journey rows: answer-key journeys proven by replay, the library, and story coverage.
function journeyTable(rows) {
  const j = rows.filter((r) => r.journeys);
  if (!j.length) return [];
  return [
    "### Journeys and story coverage",
    "",
    "A journey counts as proven only after two replays in fresh sessions, without a model, reached its outcome. Answer-key journeys inside an optional feature are left out.",
    "",
    "| App | Twin built from | Answer-key journeys proven | Library (proven / flaky / failing) | Planned, not reached | Stories covered | Not proven yet |",
    "|---|---|---|---|---|---|---|",
    ...j.map((r) => { const x = r.journeys; return `| ${r.app} | ${r.label || "-"} | ${x.proven} / ${x.real} | ${x.library} (${x.provenInLibrary} / ${x.flaky} / ${x.failing}) | ${x.planned} | ${x.storiesCovered} / ${x.stories} | ${x.missing.join(", ") || "-"} |`; }),
    "",
  ];
}

// AI understanding rows: recall (found / real) and precision (correct / proposed) per layer, after the verifier.
function understandingTable(rows) {
  const u = rows.filter((r) => r.understanding);
  if (!u.length) return [];
  const cell = (m) => `${m.found} / ${m.real} found, ${m.correct} / ${m.proposed} correct`;
  return [
    "### AI understanding (entities, features, lifecycles, rules)",
    "",
    "What a model proposed and the verifier kept, against the answer key. Each cell: recall (found / in the answer key), then precision (correct / kept).",
    "",
    "| App | Proposed | Kept | Refused by the verifier | Entities | Features | Lifecycles | Rules | Seconds |",
    "|---|---|---|---|---|---|---|---|---|",
    ...u.map((r) => { const x = r.understanding; return `| ${r.app} | ${x.proposed} | ${x.admitted} | ${x.rejected} | ${cell(x.entities)} | ${cell(x.features)} | ${cell(x.lifecycles)} | ${cell(x.rules)} | ${x.seconds} |`; }),
    "",
  ];
}

function report() {
  if (!existsSync(RESULTS)) { console.log("No results yet."); return; }
  const rows = readFileSync(RESULTS, "utf8").trim().split("\n").filter(Boolean).map((l) => JSON.parse(l))
    .filter((r) => r.status === "Completed").map((r) => ({ ...r, app: r.app ?? "shop" }));
  const tables = (app) => {
    const recall = rows.filter((r) => r.app === app && r.defects === "all-on");
    const clean = rows.filter((r) => r.app === app && r.defects === "all-off");
    return [
      "### Recall (every seeded defect on)",
      "",
      "| Configuration | Steps | Found | Confirmed | Unexplained (oracle / rule) | Intents held | Model calls | Minutes | Hunt |",
      "|---|---|---|---|---|---|---|---|---|",
      ...recall.map((r) => `| ${r.label} | ${r.steps} | ${r.detected} / ${r.seeded}${r.found?.some((x) => x.includes("(")) ? ` (${r.found.join(", ")})` : ""} | ${r.confirmedDetected} / ${r.seeded} | ${r.unexplained} / ${r.unexplainedRules} | ${r.intents ? `${r.intentsHeld} / ${r.intents}` : "-"} | ${r.modelCalls} | ${r.minutes} | ${r.hunt} |`),
      ...(recall.length ? [] : ["| _no runs yet_ | | | | | | | | |"]),
      "",
      "### False positives (every defect off)",
      "",
      "| Configuration | Steps | From oracles | From AI-written rules (flagged as probably wrong) | From twin predictions | Suspicions (never fail a build) | Model calls | Hunt |",
      "|---|---|---|---|---|---|---|---|",
      ...clean.map((r) => `| ${r.label} | ${r.steps} | ${r.unexplained} | ${r.unexplainedRules} (${r.unexplainedRulesFlagged}) | ${r.unexplainedPredictions ?? "-"} | ${r.suspicions} | ${r.modelCalls} | ${r.hunt} |`),
      ...(clean.length ? [] : ["| _no runs yet_ | | | | | | | |"]),
      "",
    ];
  };
  const lines = [
    "# Benchmarks",
    "",
    "Generated by `node scripts/benchmark.mjs` from `artifacts/benchmark-results.jsonl`. Do not edit by hand.",
    "",
    "Two demo apps with switchable seeded defects; each run starts from a freshly reset app. A seeded defect counts as",
    "**confirmed** only if its finding reproduced on both replays, without AI. **Unexplained** findings are proven findings",
    "no seeded defect accounts for: with every defect off, any such finding is a false positive.",
    "",
    "## Demo shop (`src/DemoShop`)",
    "",
    "Ten seeded defects. Six are visible to the built-in oracles: coupon stacking, expired cards accepted, zero or",
    "negative quantities, case-sensitive search, overselling, and tax truncated instead of rounded. Four need rules,",
    "roles or cross-page checks: the order desk open to shoppers, cancelling after shipping, a listed price that differs",
    "from the cart, and gift wrap charged per item (only with the gift-wrap feature on).",
    "",
    ...tables("shop"),
    "## Demo Hotel (`src/DemoHotel`)",
    "",
    "Eight seeded defects that are business rules, not generic failures: check-in in the past, check-out on or before",
    "check-in, more guests than the room sleeps, the last night not charged, the front desk open without sign-in,",
    "cancelling after check-in, a charged rate above the quoted one, and breakfast charged once instead of nightly (only",
    "with the breakfast feature on). No built-in oracle knows hotel rules, so these are found only by rules written",
    "from the stories (HOTEL-1 to HOTEL-6), roles and cross-page checks. The no-AI row is the control.",
    "",
    ...tables("hotel"),
    ...twinTables(),
    "## Method notes",
    "",
    "- Same mission, stories, secrets and setup hook in every run of an app; probes and replay confirmation on; repro shrinking off.",
    "- A broken rule is credited to a seeded defect only if the rule checks that defect's fields and the response that broke it shows the defect.",
    "- Rule findings no seeded defect explains are false positives even when the engine labelled the rule as probably wrong; the label is shown in brackets.",
    "- AI rows use the model passed with --model; results depend on it, so rows are named by strategy. Model calls are counted per hunt.",
    "- Each app also has one real bug nobody seeded: the shop returns HTTP 500 for a fractional quantity, and the hotel accepts",
    "  an email such as `john.doe@.com`. When found, they count as unexplained (a strict reading: they are real bugs, not false alarms).",
    "",
  ];
  mkdirSync("docs", { recursive: true });
  writeFileSync("docs/BENCHMARKS.md", lines.join("\n"));
  console.log(`docs/BENCHMARKS.md: ${rows.filter((r) => r.defects === "all-on").length} recall runs, ${rows.filter((r) => r.defects === "all-off").length} false-positive runs`);
}

const args = process.argv.slice(2);
if (!args.includes("--report")) {
  const only = args.includes("--only") ? args[args.indexOf("--only") + 1] : null;
  const sets = args.includes("--defects") ? DEFECT_SETS.filter((d) => d.id === args[args.indexOf("--defects") + 1]) : DEFECT_SETS;
  const app = args.includes("--app") ? args[args.indexOf("--app") + 1] : "shop";
  if (!APPS[app]) throw new Error(`Unknown app '${app}'; use ${Object.keys(APPS).join(" or ")}.`);
  for (const cfg of MATRIX.filter((c) => (only ? c.id === only : !c.ai || MODEL)))
    for (const defects of sets) await run(app, cfg, defects);
}
report();
