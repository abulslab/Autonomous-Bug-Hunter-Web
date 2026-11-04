# Bug Hunter: improvement backlog

Bug Hunter explores a web app in a real browser and reports only bugs it can prove. The principle behind every item here:
**the AI decides what is worth testing; a deterministic engine decides what is a bug.**

Status: `done` · `in progress` · `next` · `later`. Items are taken one at a time, roughly in this order.

## Done

| # | Item | Notes |
|---|------|-------|
| D1 | Browser worker (Playwright, isolated process) | Host allowlist, secrets substituted in the worker and masked everywhere, auto-started in development |
| D2 | Deterministic oracles | JS and server errors, money arithmetic, tax rounding, invalid input accepted, duplicate coupons, overselling, search misses |
| D3 | Strategies: Heuristic, Model, Hybrid | Any provider or a local Ollama model; the heuristic explorer takes over when the model fails |
| D0 | Standalone project | Own API, file storage, demo shop app, web UI and tests; models from configuration (Ollama, Claude, Gemini, OpenAI) |
| D4 | Live view and recording | MJPEG stream with cursor and click overlay, WebM replay, full-page screenshots |
| D5 | Probe routines (Phase 1) | fill-form, boundary-values, repeat-submit, case-variants, back-and-resubmit, visit |
| D6 | Rules from requirements (Phase 2) | The model writes invariants from acceptance criteria; the engine checks them on every recorded response |
| D7 | Engine probes | Recorded API calls resent with boundary values, invalid formats, repeats, stock + 1, other case, no session |
| D8 | Seeded-defect benchmark and shop reset | Detection and false positives per run; clean runs: 6/6 with no AI at 80 steps, 0 false positives with defects off |
| D9 | Replay-confirm and minimal repros (Phase 3) | Each finding replayed twice in fresh sessions with no AI: Confirmed, Flaky or Not reproduced. Confirmed repros shrunk by delta debugging over whole user actions. Clean run: 7/7 reproduced both times; repros cut from 326 to 133 steps |
| D10 | Replay setup hook (was N7) | A POST before the hunt and every replay (same host, optional bearer secret); the demo storefront has a keyed test-reset. Without it only 2 of 7 reproduced on the second replay, because the first replay changed the shop |
| D11 | Re-confirm a finished hunt | Replays the findings again, for example after a fix |
| D12 | App model and warm-up (was A1) | Survey without AI, API catalog, skill packs, app map of states and roads; Guided plans user intents with success criteria and variants from it |
| D13 | Second target app (was N1) | Demo Hotel: bookings, four business-rule defects no generic oracle sees |
| D14 | Benchmark harness and page (was N2) | `scripts/benchmark.mjs` runs every configuration on both apps, defects on and off, and generates `docs/BENCHMARKS.md`; the UI shows the same runs |
| D15 | Tracing and AI usage per hunt (was L4) | OpenTelemetry spans and metrics per hunt, step and model call; per-hunt AI budget |
| D16 | Reports and issue filing (part of L3) | Markdown, HTML, SARIF, JUnit; GitHub issues deduplicated by fingerprint |
| D17 | Architecture write-up (was L8) | C4 views, eight ADRs, evaluation method, configuration and extension guides |
| D18 | Operational baseline | OpenAPI, health checks, API key, rate limits, PostgreSQL store, Docker Compose, CI with a real smoke hunt |

## AI layer (next focus)

Principle: **AI proposes, the engine verifies.** Today AI mostly picks clicks (where small models are weakest). These items move
it to understanding, imagining and explaining, while every claim it makes is still checked on recorded evidence.

| # | Item | Why it matters |
|---|------|----------------|
| A2 | Metamorphic testing | The model proposes relations between two runs (search subsets, add-then-remove restores the total, price equal across pages, sign-out-in keeps the cart); the engine runs both sides and compares. Finds logic bugs with no expected value |
| A3 | Semantic seeded defects | Defects the deterministic oracles cannot see (price differs between pages, wrong order status, label typo, mismatched image), so the benchmark measures what AI adds |
| A4 | Vision UX review with a critic | A multimodal model reviews screenshots; a second call must agree and replay must show it again (a local vision model works) |
| A5 | AI triage | Group findings by root cause, link to the recent code change, write the bug report, deduplicate against existing tickets |
| A6 | Personas and charters | Parallel agents (malicious, novice, power, keyboard-only user) with charters planned from the app model; strong model plans, cheap model executes |
| A7 | Self-healing regression tests | Confirmed bugs become regression tests; the model repairs locators when the UI changes and replay verifies the repair |

## Next

| # | Item | Why it matters |
|---|------|----------------|
| N0 | Head-to-head with a code-generation agent | The protocol is in `docs/evaluation.md`; run it with a code-generation agent plus Playwright on both demo apps, counting found, reproduced, false positives and assertions that lock a bug in |
| N1 | Repeated benchmark runs | Several runs per AI configuration so BENCHMARKS.md shows spread, not single samples |
| N3 | Authorized targets | Prove ownership of a target (DNS TXT or a file on the site) before hunting it outside test environments |
| N4 | Probe rate limits and politeness | Requests per second per target, back-off on 429/503 |
| N5 | Hunt runner on the job queue | Hunts survive API restarts, scale across workers; secrets in a short-lived vault entry instead of process memory |
| N6 | Step-level storage | A hunt is one JSON document rewritten on every step; store steps separately to cut write amplification |
| N7 | Faster, deeper shrinking | Long repros (oversell: 87 to 58 steps) hit the 10-minute shrink budget; reuse a shared sign-in prefix, shrink in parallel sessions |

## Later

| # | Item | Why it matters |
|---|------|----------------|
| L1 | Import OpenAPI or HAR | Probe endpoints the UI never reached |
| L2 | Persistent site map and change-aware hunting | Hunt what changed since the last run; triggered from CI after a deploy |
| L3 | File confirmed bugs to Jira or Azure DevOps with deduplication | GitHub is done; the same fingerprint scheme applies |
| L5 | Authorization and IDOR probes | Swap ids between two test accounts |
| L6 | Accessibility oracles | axe-core on every snapshot |
| L7 | Probe catalog as editable files | Teams edit and version probe patterns without code changes |

## Known limits (stated, not hidden)

- Probes reach only endpoints the UI triggered at least once (L1 removes this).
- Small local models navigate poorly and may sign out mid-hunt; probes run right after the sweep and stop when the session ends.
- A model-written rule can be wrong; rules broken on most responses are marked as probably wrong.
- Storefront stories DEMO-120 to DEMO-125 describe the demo shop's rules, so rule results there are not evidence of generality (N1).
