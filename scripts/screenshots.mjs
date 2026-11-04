#!/usr/bin/env node
// Captures the UI pages used in the README into docs/images/, from a running UI and API with at least one hunt.
// Uses the Playwright that the browser worker already installs.
//
//   node scripts/screenshots.mjs H-69832f4725        # hunt to show; defaults to the latest completed hunt
//   UI=http://localhost:3100 API=http://localhost:5280 node scripts/screenshots.mjs

import { createRequire } from "node:module";
import { mkdirSync } from "node:fs";

const require = createRequire(new URL("../src/browser-worker/package.json", import.meta.url));
const { chromium } = require("playwright");

const UI = (process.env.UI ?? "http://localhost:3100").replace(/\/$/, "");
const API = (process.env.API ?? "http://localhost:5280").replace(/\/$/, "");
const hunts = await (await fetch(`${API}/api/hunter/hunts`)).json();
const hunt = process.argv[2] ?? hunts.find((h) => h.status === "Completed")?.id;
if (!hunt) throw new Error("No completed hunt to show.");

mkdirSync("docs/images", { recursive: true });
const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 1 });
async function shot(path, file, tab) {
  await page.goto(UI + path, { waitUntil: "networkidle" });
  if (tab) await page.getByRole("button", { name: tab, exact: false }).first().click();
  await page.waitForTimeout(800);
  await page.screenshot({ path: `docs/images/${file}.png` });
  console.log(`docs/images/${file}.png`);
}
await shot("/", "hunts");
await shot(`/hunts/${hunt}`, "hunt-findings", "Findings");
await shot(`/hunts/${hunt}`, "hunt-rules", "Rules");
await shot("/hunts/new", "new-hunt");
await shot("/benchmarks", "benchmarks");
await shot("/projects/hotel", "project");
await browser.close();
