#!/usr/bin/env node
// The QE workflow end to end on a demo app, with the simulators standing in for Jira and Confluence:
//   1. sprint 1 is deployed (scenario baseline) and the release gate runs: a survey of every role, the first baseline;
//   2. triage: confirmed bugs are filed in Jira, linked to the stories they break;
//   3. sprint 2 is deployed (a story changes in Jira, a feature ships with a bug, a regression, a fix);
//   4. the gate runs again: the changed story is hunted first; the report says what changed since the baseline;
//   5. the readiness report is published to Confluence.
//
//   node scripts/workflow-demo.mjs [--app hotel] [--scenario sprint-2] [--steps 40]
//
// Needs the dev stack (node scripts/dev.mjs). No model is used.

import { spawnSync } from "node:child_process";

const args = process.argv.slice(2);
const opt = (n, d = null) => (args.includes(`--${n}`) ? args[args.indexOf(`--${n}`) + 1] : d);
const app = opt("app", "hotel");
const scenario = opt("scenario", "sprint-2");
const steps = opt("steps", "40");
const API = (process.env.API ?? "http://localhost:5280").replace(/\/$/, "") + "/api";
const headers = { "content-type": "application/json", ...(process.env.BUGHUNTER_API_KEY ? { "x-api-key": process.env.BUGHUNTER_API_KEY } : {}) };
const here = new URL(".", import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, "$1");

const node = (script, ...a) => {
  const r = spawnSync(process.execPath, [`${here}${script}`, ...a], { stdio: "inherit" });
  return r.status ?? 1;
};
const call = async (path, method = "GET", body) => {
  const res = await fetch(API + path, { method, headers, body: body ? JSON.stringify(body) : undefined });
  const text = await res.text();
  if (!res.ok) throw new Error(`${method} ${path}: ${res.status} ${text.slice(0, 200)}`);
  return text ? JSON.parse(text) : null;
};
const step = (s) => console.log(`\n=== ${s}`);

step(`1. Sprint 1 deployed (${scenario} baseline); release gate`);
if (node("scenario.mjs", "--app", app, "--scenario", scenario, "baseline")) process.exit(1);
node("ci-gate.mjs", "--project", app, "--steps", steps, "--survey");

step("2. Triage: confirmed bugs filed in Jira");
const runs = await call(`/projects/${app}/runs`);
const run1 = await call(`/projects/${app}/runs/${runs[0].id}`);
for (const hid of run1.run.hunts) {
  const h = (await call(`/hunter/hunts/${hid}`)).hunt;
  for (const f of h.findings.filter((x) => x.reproduced === "Confirmed")) {
    const issue = await call(`/projects/${app}/findings/${hid}/${f.id}/jira`, "POST");
    console.log(`  ${issue.key}${issue.created ? "" : " (already filed)"}: ${f.title}${f.requirement ? ` -> relates to ${f.requirement}` : ""}`);
  }
}

// The QE lead releases sprint 1 with its bugs filed: run 1 becomes the baseline sprint 2 is compared with.
await call(`/projects/${app}/runs/${run1.run.id}/accept`, "POST");
console.log(`  run ${run1.run.id} accepted as the baseline (sprint 1 released with its bugs filed)`);

step(`3. Sprint 2 deployed (${scenario} changes)`);
if (node("scenario.mjs", "--app", app, "--scenario", scenario, "apply")) process.exit(1);
const sync = await call(`/projects/${app}/sync`, "POST");
console.log(`  knowledge v${sync.version}: ${sync.diff.storiesChanged.map((c) => `${c.key} v${c.from} -> v${c.to}`).join(", ") || "no story changes"}`);
const impact = await call(`/projects/${app}/impact`);
console.log(`  impact: features ${impact.features.join(", ") || "-"}; endpoints ${impact.endpoints.join(", ") || "-"}; stale rules ${impact.rules.length}; journeys to re-prove ${impact.journeys.join(", ") || "-"}`);

step("4. Release gate after the deploy: the change first");
const gate = node("ci-gate.mjs", "--project", app, "--steps", steps, "--out", `readiness-${app}.md`);

step("5. Readiness report to Confluence");
const pub = await call(`/projects/${app}/readiness/publish`, "POST");
console.log(`  ${pub.verdict}: ${pub.url} (version ${pub.version}); also written to readiness-${app}.md`);
const review = await call(`/projects/${app}/review`);
console.log(`  ${review.pending} items wait for review in the Review tab.`);
process.exit(gate);
