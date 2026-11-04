#!/usr/bin/env node
// Drives a demo app through a benchmark scenario (ground-truth/<app>/scenarios/<id>.json): the baseline for run 1, then
// the changes of the next sprint (story edits, features, defects switched on and off) before run 2.
//
//   node scripts/scenario.mjs --app hotel --scenario sprint-2 baseline   # reset everything, then the run-1 state
//   node scripts/scenario.mjs --app hotel --scenario sprint-2 apply      # the sprint's changes, then fresh test data
//   node scripts/scenario.mjs --app hotel status                         # stories, defects and features now
//
// The app's URL defaults to the local demo port; the harness key comes from DEMO_SHOP_KEY and the simulators' token from
// SIM_TOKEN, read from the environment or the .env file that scripts/dev.mjs creates.
// Story edits also go to the Jira simulator (--sim, default http://localhost:5292; skipped if it is not running), the
// way a product owner would change a story, so a project that syncs from Jira sees the change.

import { readFileSync } from "node:fs";
import { loadEnv } from "./env.mjs";

const PORTS = { shop: 5290, hotel: 5291 };
const args = process.argv.slice(2);
const opt = (name) => (args.includes(`--${name}`) ? args[args.indexOf(`--${name}`) + 1] : null);
const app = opt("app");
const command = args.filter((a, i) => !a.startsWith("--") && !args[i - 1]?.startsWith("--"))[0];
if (!PORTS[app] || !["baseline", "apply", "status"].includes(command))
  throw new Error("Usage: node scripts/scenario.mjs --app shop|hotel [--scenario id] baseline|apply|status");
const base = (opt("url") ?? `http://localhost:${PORTS[app]}`).replace(/\/$/, "") + "/api";
loadEnv();
const key = process.env.DEMO_SHOP_KEY;
if (!key) throw new Error("Set DEMO_SHOP_KEY (node scripts/dev.mjs creates .env with one).");
const sim = (opt("sim") ?? "http://localhost:5292").replace(/\/$/, "");
const simToken = process.env.SIM_TOKEN ?? "";
const adf = (text) => ({ type: "doc", version: 1, content: text.split("\n").map((line) => ({ type: "paragraph", content: line ? [{ type: "text", text: line }] : [] })) });

async function simCall(path, method, body, token) {
  try {
    const res = await fetch(sim + path, { method, headers: { "content-type": "application/json", authorization: `Bearer ${token}` }, body: body ? JSON.stringify(body) : undefined });
    if (!res.ok) throw new Error(`${method} ${path} -> ${res.status}`);
    return true;
  } catch (e) {
    if (e.cause?.code === "ECONNREFUSED" || /fetch failed/.test(e.message)) return false; // simulators not running
    throw e;
  }
}

async function call(path, method = "GET", body) {
  const res = await fetch(base + path, { method, headers: { "content-type": "application/json", authorization: `Bearer ${key}` }, body: body ? JSON.stringify(body) : undefined });
  const text = await res.text();
  if (!res.ok) throw new Error(`${method} ${path} -> ${res.status}: ${text.slice(0, 200)}`);
  return text ? JSON.parse(text) : null;
}

function load() {
  const id = opt("scenario");
  if (!id) throw new Error("--scenario is required for baseline and apply.");
  const s = JSON.parse(readFileSync(new URL(`../ground-truth/${app}/scenarios/${id}.json`, import.meta.url), "utf8"));
  if (s.app !== app) throw new Error(`Scenario ${id} is for ${s.app}, not ${app}.`);
  return s;
}

async function status() {
  const [stories, defects, features] = await Promise.all([call("/stories"), call("/admin/defects"), call("/test/features")]);
  console.log("stories:  " + stories.map((s) => `${s.key} v${s.version}`).join(", "));
  console.log("defects:  " + (defects.filter((d) => d.enabled).map((d) => d.id).join(", ") || "none"));
  console.log("features: " + (features.filter((f) => f.enabled).map((f) => f.id).join(", ") || "none"));
}

if (command === "baseline") {
  const s = load();
  await call("/test/reset-all", "POST");
  if (await simCall("/sim/reset", "POST", null, key)) console.log("  simulators reset");
  const defects = await call("/admin/defects");
  await call("/admin/defects", "PUT", Object.fromEntries(defects.map((d) => [d.id, s.baseline.defects[d.id] === true])));
  if (Object.keys(s.baseline.features).length) await call("/test/features", "PUT", s.baseline.features);
  console.log(`${app}: baseline of ${s.id} set.`);
} else if (command === "apply") {
  const s = load();
  for (const c of s.changes) {
    if (c.kind === "story") {
      await call(`/test/stories/${c.key}`, "PUT", { title: c.title, criteria: c.criteria });
      await simCall(`/rest/api/3/issue/${c.key}`, "PUT", { fields: { summary: c.title, description: adf(c.criteria) } }, simToken);
    }
    else if (c.kind === "feature") await call("/test/features", "PUT", { [c.id]: c.on });
    else if (c.kind === "defect") await call("/admin/defects", "PUT", { [c.id]: c.on });
    else throw new Error(`Unknown change kind '${c.kind}'.`);
    console.log(`  ${c.kind} ${c.key ?? c.id}${c.on === undefined ? "" : c.on ? " on" : " off"}: ${c.why}`);
  }
  await call("/test/reset", "POST");
  console.log(`${app}: changes of ${s.id} applied.`);
}
await status();
