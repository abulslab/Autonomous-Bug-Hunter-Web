#!/usr/bin/env node
// Starts a hunt from a project and waits for it: the project supplies the URL, reset hook, role account, stories and
// documents. Prints the outcome and exits non-zero if the hunt did not complete.
//
//   node scripts/project-hunt.mjs --project hotel --role front-desk [--strategy Heuristic] [--steps 60] [--model ollama:qwen2.5:7b]

const args = process.argv.slice(2);
const opt = (n, d = null) => (args.includes(`--${n}`) ? args[args.indexOf(`--${n}`) + 1] : d);
const API = (process.env.API ?? "http://localhost:5280").replace(/\/$/, "") + "/api";
const headers = { "content-type": "application/json", ...(process.env.BUGHUNTER_API_KEY ? { "x-api-key": process.env.BUGHUNTER_API_KEY } : {}) };
const project = opt("project");
if (!project) throw new Error("Usage: node scripts/project-hunt.mjs --project id [--role id] [--strategy s] [--steps n] [--model m]");
const strategy = opt("strategy", "Heuristic");
const model = opt("model");

const res = await fetch(`${API}/projects/${project}/hunts`, {
  method: "POST", headers,
  body: JSON.stringify({ role: opt("role"), strategy, connection: strategy === "Heuristic" ? "heuristic" : model, plannerConnection: model, maxSteps: Number(opt("steps", "60")), maxCostUsd: 1 }),
});
const started = await res.json();
if (!res.ok) throw new Error(`${res.status}: ${JSON.stringify(started)}`);
process.stdout.write(`${project} ${opt("role") ?? "(default role)"} ${strategy}: hunt ${started.id} `);
let d;
for (;;) {
  d = await (await fetch(`${API}/hunter/hunts/${started.id}`, { headers })).json();
  if (!d.active && !["Queued", "Running", "Confirming"].includes(d.hunt.status)) break;
  process.stdout.write(".");
  await new Promise((r) => setTimeout(r, 10000));
}
const h = d.hunt;
console.log(` ${h.status}${h.error ? `: ${h.error}` : ""}; ${h.steps.length} steps, ${h.findings.filter((f) => f.reproduced === "Confirmed").length} confirmed findings, twin v${h.twinVersion ?? "-"}`);
for (const n of h.notes.filter((n) => n.startsWith("App twin"))) console.log(`  ${n}`);
if (h.status !== "Completed") process.exit(1);
