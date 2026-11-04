#!/usr/bin/env node
// Ground-truth smoke test: a real hunt against the demo shop with every seeded defect on and no AI.
// Passes only if Bug Hunter finds enough seeded defects, every one of them reproduces on replay, and nothing is
// reported that the seeded defects do not explain (a false-positive signal). Used by CI; runnable locally.
//
//   node scripts/smoke-hunt.mjs            # API on http://localhost:5280
//   API=http://host:5280 STEPS=80 MIN_DETECTED=5 node scripts/smoke-hunt.mjs

import { writeFileSync, mkdirSync } from "node:fs";

const API = (process.env.API ?? "http://localhost:5280").replace(/\/$/, "") + "/api/hunter";
const STEPS = Number(process.env.STEPS ?? 80);
const MIN_DETECTED = Number(process.env.MIN_DETECTED ?? 5);
const TIMEOUT_MIN = Number(process.env.TIMEOUT_MIN ?? 30);
const headers = { "content-type": "application/json", ...(process.env.BUGHUNTER_API_KEY ? { "x-api-key": process.env.BUGHUNTER_API_KEY } : {}) };

async function call(path, body, method) {
  const res = await fetch(API + path, { method: method ?? (body ? "POST" : "GET"), headers, body: body ? JSON.stringify(body) : undefined });
  const text = await res.text();
  if (!res.ok) throw new Error(`${method ?? (body ? "POST" : "GET")} ${path} -> ${res.status}: ${text.slice(0, 300)}`);
  return text ? JSON.parse(text) : null;
}

const status = await call("/status");
if (!status.storefront?.enabled) throw new Error("The demo shop is not reachable from the API.");
const shop = status.storefront;
await call("/demo/defects", Object.fromEntries(status.defects.map((d) => [d.id, !d.requires])), "PUT"); // feature-only defects stay off

const { id } = await call("/hunts", {
  url: shop.url, strategy: "Heuristic", maxSteps: STEPS, probe: true, confirm: true, shrink: false,
  mission: "Find functional bugs in browsing, search, cart, coupons and checkout.",
  secrets: { email: "shopper01@shop.demo", password: "Shop123!" }, // the server fills in the reset key
  setupUrl: shop.resetUrl, setupSecret: "reset-key",
});
console.log(`hunt ${id}: ${STEPS} steps, no AI, all seeded defects on`);

const deadline = Date.now() + TIMEOUT_MIN * 60_000;
let hunt;
for (;;) {
  const d = await call(`/hunts/${id}`);
  hunt = d.hunt;
  if (!d.active && !["Queued", "Running", "Confirming"].includes(hunt.status)) break;
  if (Date.now() > deadline) throw new Error(`Hunt did not finish within ${TIMEOUT_MIN} minutes (status ${hunt.status}).`);
  await new Promise((r) => setTimeout(r, 10_000));
}

mkdirSync("artifacts", { recursive: true });
writeFileSync(`artifacts/smoke-hunt-${id}.json`, JSON.stringify(hunt, null, 2));
// The same exports a pipeline would publish: SARIF for code scanning, JUnit for the CI test view, Markdown for people.
for (const [format, ext] of [["sarif", "sarif"], ["junit", "xml"], ["md", "md"]]) {
  const res = await fetch(`${API}/hunts/${id}/report?format=${format}`, { headers });
  if (res.ok) writeFileSync(`artifacts/smoke-hunt-${id}.${ext}`, await res.text());
}

const b = hunt.benchmark;
const proven = hunt.findings.filter((f) => f.provenance === "Observed");
const unconfirmed = proven.filter((f) => f.reproduced !== "Confirmed");
console.log(`status ${hunt.status}; detected ${b.detected}/${b.total} seeded defects, ${b.confirmedDetected} confirmed; unexplained ${b.unmatchedObserved}`);
for (const f of proven) console.log(`  ${f.reproduced ?? "-"}  ${f.key}`);
for (const n of hunt.notes) console.log(`  note: ${n}`);

const failures = [];
if (hunt.status !== "Completed") failures.push(`hunt ended as ${hunt.status}${hunt.error ? `: ${hunt.error}` : ""}`);
if (b.detected < MIN_DETECTED) failures.push(`found ${b.detected} seeded defects, expected at least ${MIN_DETECTED}`);
if (unconfirmed.length) failures.push(`${unconfirmed.length} proven finding(s) did not reproduce on replay: ${unconfirmed.map((f) => f.key).join(", ")}`);
if (b.unmatchedObserved > 0) failures.push(`${b.unmatchedObserved} proven finding(s) not explained by a seeded defect`);
if (failures.length) { console.error("SMOKE HUNT FAILED:\n- " + failures.join("\n- ")); process.exit(1); }
console.log("SMOKE HUNT PASSED");
