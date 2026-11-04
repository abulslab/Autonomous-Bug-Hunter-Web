#!/usr/bin/env node
// The release gate a CI pipeline runs after a deploy: sync the project's knowledge, hunt what changed first (or survey
// every role when nothing changed), wait for the run, then read the release readiness report. Writes the report as
// Markdown (to --out, and to the GitHub step summary when running in Actions) and exits non-zero when the verdict is
// "not-ready" (or also "at-risk" with --strict).
//
//   node scripts/ci-gate.mjs --project hotel [--env local] [--steps 60] [--strict] [--out readiness.md] [--publish] [--survey]
//
// API defaults to http://localhost:5280; BUGHUNTER_API_KEY is sent as x-api-key when set. --publish also publishes the
// report to Confluence. No model is used unless --model is given (the gate's hunts are no-AI by default).

import { appendFileSync, writeFileSync } from "node:fs";

const args = process.argv.slice(2);
const opt = (n, d = null) => (args.includes(`--${n}`) ? args[args.indexOf(`--${n}`) + 1] : d);
const flag = (n) => args.includes(`--${n}`);
const API = (process.env.API ?? "http://localhost:5280").replace(/\/$/, "") + "/api";
const headers = { "content-type": "application/json", ...(process.env.BUGHUNTER_API_KEY ? { "x-api-key": process.env.BUGHUNTER_API_KEY } : {}) };
const project = opt("project");
if (!project) throw new Error("Usage: node scripts/ci-gate.mjs --project id [--env name] [--steps n] [--model m] [--strict] [--out file] [--publish]");
const model = opt("model");
const steps = Number(opt("steps", "60"));

async function call(path, method = "GET", body) {
  const res = await fetch(API + path, { method, headers, body: body ? JSON.stringify(body) : undefined });
  const text = await res.text();
  return { status: res.status, ok: res.ok, body: text && res.headers.get("content-type")?.includes("json") ? JSON.parse(text) : text };
}
const must = (r, what) => { if (!r.ok) throw new Error(`${what}: ${r.status} ${typeof r.body === "string" ? r.body : JSON.stringify(r.body)}`); return r.body; };

const sync = must(await call(`/projects/${project}/sync`, "POST"), "sync");
console.log(`knowledge v${sync.version}${sync.changed ? " (changed)" : ""}`);

const strategy = model ? "Hybrid" : "Heuristic";
let runId;
// --survey: every role, whatever changed (a release candidate, or the first run).
const changed = flag("survey") ? { status: 409 } : await call(`/projects/${project}/hunts/changed`, "POST", { environment: opt("env"), strategy, connection: model ?? "heuristic", maxSteps: steps });
if (changed.ok) {
  runId = `R-${changed.body.id.slice(2)}`;
  console.log(`change-driven hunt ${changed.body.id}: ${changed.body.mission}`);
} else if (changed.status === 409) {
  const s = must(await call(`/projects/${project}/survey`, "POST", { environment: opt("env"), maxSteps: steps, confirm: true }), "survey");
  runId = s.run;
  console.log(`nothing changed; survey of every role: ${s.hunts.map((h) => `${h.role} ${h.hunt}`).join(", ")}`);
} else must(changed, "change-driven hunt");

process.stdout.write(`run ${runId} `);
for (let i = 0; ; i++) {
  const r = must(await call(`/projects/${project}/runs/${runId}`), "run");
  if (r.finished) break;
  if (i > 360) throw new Error("The run did not finish within an hour.");
  process.stdout.write(".");
  await new Promise((res) => setTimeout(res, 10000));
}
console.log(" finished");

const report = must(await call(`/projects/${project}/readiness`), "readiness");
const md = must(await call(`/projects/${project}/readiness?format=markdown`), "readiness markdown");
if (opt("out")) writeFileSync(opt("out"), md);
if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, md + "\n");
if (flag("publish")) {
  const p = await call(`/projects/${project}/readiness/publish`, "POST");
  console.log(p.ok ? `published to Confluence: ${p.body.url} (v${p.body.version})` : `publishing failed: ${p.status}`);
}
console.log(`\nverdict: ${report.verdict}`);
for (const b of report.blockers) console.log(`  blocker: ${b}`);
for (const r of report.risks) console.log(`  risk: ${r}`);
process.exit(report.verdict === "not-ready" || (flag("strict") && report.verdict === "at-risk") ? 1 : 0);
