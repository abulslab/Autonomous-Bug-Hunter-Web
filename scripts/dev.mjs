#!/usr/bin/env node
// Starts every app needed for development and waits until each answers, then prints where they are. Log lines are
// prefixed with the app's name; Ctrl+C stops them all.
//
//   node scripts/dev.mjs                    # everything
//   node scripts/dev.mjs --only api,shop    # a subset (names below); the API still starts the browser worker itself
//   node scripts/dev.mjs --no-build         # skip the .NET build (after building once)

import { spawn, spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import path from "node:path";
import { ensureEnv } from "./env.mjs";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const args = process.argv.slice(2);
const only = args.includes("--only") ? args[args.indexOf("--only") + 1].split(",") : null;
const noBuild = args.includes("--no-build");
const win = process.platform === "win32";

// Local keys (DEMO_SHOP_KEY, SIM_TOKEN): random values in a git-ignored .env, created on first run, then loaded.
const created = ensureEnv();
if (created.length) console.log(`Created .env with new random ${created.join(" and ")} (git-ignored; keep it local).`);

const APPS = [
  { name: "simulators", cmd: "dotnet", args: ["run", "--project", "src/Simulators"], url: "http://localhost:5292/", what: "Jira and Confluence simulators" },
  { name: "shop", cmd: "dotnet", args: ["run", "--project", "src/DemoShop"], url: "http://localhost:5290/", what: "Demo shop" },
  { name: "hotel", cmd: "dotnet", args: ["run", "--project", "src/DemoHotel"], url: "http://localhost:5291/", what: "Demo Hotel" },
  { name: "api", cmd: "dotnet", args: ["run", "--project", "src/BugHunter.Api"], url: "http://localhost:5280/readyz", show: "http://localhost:5280/docs", what: "API (starts the browser worker)" },
  { name: "web", cmd: "npm", args: ["--prefix", "src/web", "run", "dev"], url: "http://localhost:3100/", what: "UI" },
].filter((a) => !only || only.includes(a.name));
if (only && APPS.length !== only.length) throw new Error(`Unknown app in --only; use: simulators, shop, hotel, api, web.`);

const colors = [36, 33, 35, 32, 34];
const children = [];

function prefix(app, i, chunk) {
  for (const line of chunk.toString().split(/\r?\n/)) if (line.trim()) process.stdout.write(`\x1b[${colors[i % colors.length]}m${app.name.padEnd(10)}\x1b[0m ${line}\n`);
}

// One build for the whole solution, so the apps do not race to build the same projects.
const dotnetApps = APPS.filter((a) => a.cmd === "dotnet");
if (dotnetApps.length && !noBuild) {
  console.log("Building the solution...");
  const b = spawnSync("dotnet", ["build", "BugHunter.sln", "-v", "q", "-nologo"], { cwd: root, stdio: "inherit" });
  if (b.status !== 0) process.exit(b.status ?? 1);
}

APPS.forEach((app, i) => {
  const a = app.cmd === "dotnet" ? [...app.args, "--no-build"] : app.args;
  const env = { ...process.env, Hunter__MaxMinutes: process.env.Hunter__MaxMinutes ?? "20" };
  // npm is a .cmd script on Windows, which only a shell can start; it gets one command string (its arguments are fixed).
  const child = win && app.cmd === "npm" ? spawn([app.cmd, ...a].join(" "), { cwd: root, shell: true, env }) : spawn(app.cmd, a, { cwd: root, env });
  child.stdout.on("data", (d) => prefix(app, i, d));
  child.stderr.on("data", (d) => prefix(app, i, d));
  child.on("exit", (code) => { if (!stopping) console.log(`${app.name} exited (${code}).`); });
  children.push(child);
});

let stopping = false;
function stop() {
  if (stopping) return;
  stopping = true;
  console.log("\nStopping...");
  for (const c of children) {
    if (c.exitCode !== null) continue;
    // On Windows the shell wrapper's children survive a plain kill; take the whole process tree.
    if (win) spawnSync("taskkill", ["/pid", String(c.pid), "/T", "/F"], { stdio: "ignore" });
    else c.kill("SIGINT");
  }
  setTimeout(() => process.exit(0), 1500);
}
process.on("SIGINT", stop);
process.on("SIGTERM", stop);

async function healthy(url, deadline) {
  while (Date.now() < deadline) {
    try { if ((await fetch(url)).ok) return true; } catch { /* not up yet */ }
    await new Promise((r) => setTimeout(r, 1000));
  }
  return false;
}

const deadline = Date.now() + 180_000;
const results = await Promise.all(APPS.map(async (a) => ({ a, ok: await healthy(a.url, deadline) })));
console.log("\n  App          URL                              Status");
for (const { a, ok } of results) console.log(`  ${a.name.padEnd(12)} ${(a.show ?? a.url).padEnd(32)} ${ok ? "ready" : "NOT READY"}  ${a.what}`);
console.log(results.every((r) => r.ok) ? "\nAll apps are up. Ctrl+C stops them." : "\nSome apps did not answer within 3 minutes; see their log lines above.");
