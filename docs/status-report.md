# Status report

Date: 2026-04-11. Version 0.2.0 (see the [changelog](../CHANGELOG.md)). This page says where the project stands,
what was measured, what went wrong on the way, and what is open. Numbers come from
[BENCHMARKS.md](BENCHMARKS.md); the method is in [evaluation.md](evaluation.md).

## 1. Where it stands

| Area | State |
|---|---|
| Engine: oracles, probes, rules, replay confirmation, shrinking | Stable. With no AI it confirms all six seeded shop defects at 80 steps, with no false positives when every defect is off |
| Strategies | Heuristic and Hybrid stable; Guided (warm-up, intents, navigator) experimental |
| Ground truth | Two demo apps: the shop (oracle-visible defects) and Demo Hotel (business-rule defects only rules can catch) |
| Operational baseline | OpenAPI, health checks, API key, rate limits, OpenTelemetry, PostgreSQL store, Docker Compose, CI with a real smoke hunt |
| Integrations | Markdown, HTML, SARIF and JUnit reports; GitHub issues with deduplication |
| Projects and knowledge | Projects with environments, roles and write-only secrets; stories and documents synced from the Jira and Confluence simulators (or files), versioned, with criterion-level diffs; hunts record the story versions they tested |
| App twin | A versioned model of each project's app, built from its hunts with no AI: pages, routes and API endpoints, each with evidence, a status, the versions it was seen in and the roles it was seen as; statuses per role; unknowns |
| AI understanding | A model proposes entities, features, lifecycles and rules from the twin and the knowledge; a deterministic verifier keeps only what recorded or declared evidence supports, and records why it refuses the rest |
| Predictive twin | Every project hunt checks responses against lifecycle transitions (from the twin), role-only areas (from the project's roles) and listed-price consistency, and runs the probes these make possible (another listed item, another role's endpoints, a disallowed transition); mismatches are twin-mismatch findings, confirmed by replay |
| Workflow integration and release readiness | The readiness verdict (ready, at risk, not ready) is computed from proven state with no model: open confirmed bugs, regressed journeys and blocking reviews block; unconfirmed findings, known issues, untested changes, waiting reviews, low coverage and unexplained changes are risks. `scripts/ci-gate.mjs` runs the gate from CI (example workflow in `docs/examples/`). Findings are filed as Jira bugs linked to the stories they break (once per bug); the report is published to Confluence in its own space. `scripts/workflow-demo.mjs` runs sprint 1, triage, sprint 2 and release end to end |
| Change-driven hunting and learning | A knowledge sync that changes stories traces them to features, rules, journeys and endpoints: the stories' rules go stale (not checked until understanding rewrites them and review accepts them), affected journeys are proven again. "Hunt the change first" starts a hunt on the changed stories, the affected endpoints and areas new in the twin since the baseline. Triage outcomes (bug, not a bug, known issue, won't fix) are kept per finding: not a bug and won't fix are suppressed in later runs, bugs make their endpoint a priority in later missions. Accepted rules form the rule library, reused without AI |
| Review gate | AI-drafted rules, proven journeys, unexplained behaviour changes and baseline updates wait for a decision; people decide in the Review tab, every decision logged and reversible. An LLM judge (another model family than the planner) decides only with bypass on and measured agreement with people above a threshold; a share of its decisions is spot-checked; behaviour changes and blocking baseline updates always need a person. Runs that bring only improvements advance the baseline by themselves |
| Runs and baselines | A run is the hunts started together (a survey or one hunt); its snapshot (proven findings, journey statuses, endpoint behaviour per role, story versions and coverage) is compared with the baseline: new, fixed and still-open findings, regressed and recovered journeys, behaviour changed with or without a story change, endpoints added or removed, coverage gained or lost. Proven journeys replay in every hunt of their role |
| Journeys | Happy paths mined from project hunts (the shortest path to each successful write, per role), proven only by two replays without a model; a Playwright test per proven journey; planned journeys for twin features nothing reaches yet; story coverage |
| Measurement | No-AI rows current. AI rows depend on the model a team brings; pass `--model provider:model` to the harness to measure yours |

## 2. Results

Current build, corrected scoring. "Confirmed" means the finding reproduced on both replays without AI.

| App | Configuration | Seeded defects confirmed | Unexplained with defects off (oracle / rule) |
|---|---|---|---|
| Demo shop | No AI, 80 steps | 6 / 9 | 0 / 0 |
| Demo Hotel | No AI, 80 steps (control) | 0 / 7 | 0 / 0 |

App twin after two no-AI hunts per app (one per role, 60 steps), scored against the answer keys:

| App | Endpoint recall | Endpoint precision | Page recall |
|---|---|---|---|
| Demo Hotel | 6 / 10 (60%) | 100% | 4 / 4 (100%) |
| Demo shop | 11 / 18 (61%) | 100% | 9 / 9 (100%) |

Everything the twin knows is real and every page was found; the missing endpoints are actions the sweep does not
reach in 60 steps (cancel, coupons, checkout, check-in).

After P3 (each app's OpenAPI document imported, then one no-AI survey hunt per role, with only the seeded permission
defect switched on):

| App | Endpoint recall | Endpoint precision | Page recall | Permission checks as specified | Differences |
|---|---|---|---|---|---|
| Demo Hotel | 10 / 10 | 100% | 4 / 4 | 10 / 13 | the guest reaches the three front-desk endpoints: the seeded `desk-open` defect |
| Demo shop | 18 / 18 | 100% | 9 / 9 | 18 / 18 | none; the seeded `staff-open` defect is not exercised (below) |

AI understanding with a small local model on CPU (three runs merged, about 3 to 5 minutes each), after
the verifier, against the answer keys (recall: found / in the key; precision: correct / kept):

| App | Entities | Features | Lifecycles | Rules |
|---|---|---|---|
| Demo Hotel | 2 / 3, 100% | 7 / 7, 100% | 1 / 1, 100% | 4 / 6, 100% |
| Demo shop | 3 / 5, 100% | 6 / 9, 100% | 1 / 1, 100% | 2 / 7, 33% |

What the verifier refused had a reason each time: endpoints the app does not have (GET /api/bookings/{id}), fields an
endpoint does not return, checks that do not parse. Measuring it found a bug in the verifier itself (ids in
`{placeholder}` paths were escaped and never matched, so every lifecycle was refused), fixed with a regression test.
Shop rule precision is low because a kept rule counts as wrong when the answer key does not list it.

Predictive twin, one no-AI survey per role (120 steps, replay confirmation on), with the twin built in P2 to P4:

| App | Seeded defects confirmed | Found by | False positives with defects off (oracle / rule / prediction) |
|---|---|---|---|
| Demo Hotel | 5 / 7 | rules from the twin: check-out before check-in, over capacity, last night free; predictions: front desk open (permission), quoted rate (consistency) | 0 / 0 / 0 |

Not found on the hotel: past check-in (no admitted rule compares dates in a way the sweep exercised) and cancel after
check-in (no hunt held a checked-in booking of its own). The shop was not re-measured in this phase: in development,
phases get a smoke check and the full benchmark runs after P10. An earlier shop run showed two limits worth naming:
the local model's lifecycle had no cancel transition, so cancel-after-shipping cannot be predicted, and an AI-written
rule ("a quantity below 1 is rejected") disagrees with the shop removing a line set to 0, a product question for the
review gate (P8). Scoring was also tightened: a cross-endpoint defect can only be credited to a consistency
prediction, never to a rule on one response.

Run comparison, smoke check: two hotel surveys (30 steps per role), the front desk opened between them. Run B against
the baseline (run A, accepted): one new finding, "GET /api/staff/bookings answers the guest role"; the journeys that pass
through the front-desk sign-in regressed, correctly, because with the defect on the desk no longer shows its sign-in
form. That also shows a limit: journeys keep their whole recorded path (40 to 85 steps), which makes them brittle;
shrinking them to the steps the outcome needs is a follow-up.

Journeys after one no-AI survey per role (120 steps), all defects off:

| App | Answer-key journeys proven | Journey library | Stories covered |
|---|---|---|---|
| Demo Hotel | 2 / 4 (book, cancel) | 4, all proven 2 / 2 | 4 / 6 |
| Demo shop | 3 / 5 (buy, buy with a coupon, cancel) | 8, all proven 2 / 2 | 3 / 8 |

Not proven yet: the front desk's check-in and check-out and the staff's ship-and-deliver (the sweep does not reach
them; they are listed as planned journeys), and the shop's "find a product", a read that the miner, which mines
writes, does not take as an outcome. Journeys are matched to the answer key by role and outcome, so one proven
checkout counts for both checkout journeys.

Measuring journeys found a sweep bug: the "plausible last" convention held for numbers and dates but not for names,
emails, card numbers and ZIP codes, so the final form round sent a 300-character name and "not-an-email" and every
hotel booking was rejected. Forms now try one invalid value at a time with the other fields plausible, then one fully
plausible submission; the no-AI benchmark rows were re-measured unchanged (shop 6 / 9, hotel 0 / 7, no false positives).

Two things the first scoring showed. The permission check found two mistakes in the answer keys themselves (it said
only guests can list hotel bookings and only visitors can sign in to the shop; the front desk uses a browser too, and
signing in again is ordinary); both keys were corrected. And the shop's open order desk stays invisible to a UI-driven
survey, because the UI hides the order desk from shoppers and nothing resends the staff calls with a shopper's
session; P6 adds that cross-role replay.

Measured after P0 widened the ground truth (more defects, a second role per app, answer keys). The local-model rows
below were measured on the earlier ground truth (6 shop and 4 hotel defects) and are kept for what they showed; they
are re-run in later phases.

| App (earlier ground truth) | Configuration | Seeded defects confirmed | Unexplained with defects off (oracle / rule) |
|---|---|---|---|
| Demo shop | Guided, small local model, 40 steps | 4 / 6 | 0 / 2 |
| Demo shop | Hybrid, small local model, 40 steps | 1 / 6 | not run |
| Demo Hotel | Guided, small local model, 40 steps | 0 / 4 | 1 / 2 |
| Demo Hotel | Hybrid, small local model, 40 steps | 3 / 4 | not run |

Single runs each; read them as plumbing checks, not as a measure of the design. What they show:

- **On the hotel, AI rules are the only way in.** The no-AI control records bookings with past and reversed dates,
  zero guests and a free last night, and reports nothing; Hybrid with the local model wrote rules from the stories that
  turned three of those into confirmed findings. Guided with the same model found none: its rules were partly wrong
  and its replies often failed the action contract, so it barely booked.
- **On the shop, the engine does the work.** Every local-model credit came from built-in oracles; at 40 steps neither
  AI strategy matched the no-AI sweep at 80.
- **Wrong rules are the false-positive risk.** All rule-based unexplained findings with defects off came from rules the
  local model wrote wrongly (for example, expecting every booking to fail); the engine labelled some, not all, as
  probably wrong. The one oracle finding on the hotel is a real bug nobody seeded: it accepts the email `john.doe@.com`.

Hosted-model runs are measured by passing `--model provider:model` to the benchmark harness (`node scripts/benchmark.mjs --app hotel --model provider:model`).


## 3. What the second app taught us

Demo Hotel was added to answer "is this tuned to the demo shop?". It found four problems in Bug Hunter itself before
it produced a single trustworthy number. Each is fixed and has a test.

| # | What happened | Cause | Fix |
|---|---|---|---|
| 1 | The no-AI sweep never booked a room and stopped after 28 of 80 steps | Its last search variant (1,000 guests) left an empty room list, and a form never ended on a valid value (the plausible value was past the round limit); date fields got "Test value" | The last round uses each field's plausible value; dates get a past and reversed range, then a valid one |
| 2 | Guided with a hosted model: 0 of 4, although the model wrote the right rules | Planning ran before the first booking, and admission rejected rules for endpoints not seen yet, so all booking rules were dropped | Rules for an endpoint not reached yet, under the API the app already uses, wait for it and are checked from its first response |
| 3 | The next Guided run scored "4 of 4" | Scoring credited a broken rule to every seeded defect its response happened to show (a capacity rule got credit for the date bug) | Credit only for defects whose fields the rule checks; the honest score of that run was 2 of 4 |
| 4 | A Hybrid run with every defect off showed 0 false positives | A wrong AI rule (`count(coupons) == len(coupons)`, never true) produced a confirmed finding, but only oracle findings were counted | False positives from AI-written rules are counted and shown, including the ones the engine labelled as a probably wrong rule |

Also fixed while measuring: the browser worker took snapshots of single-page apps before same-document navigation had
rendered; a hunt waiting on slow model calls lost its browser session after ten idle minutes (the worker's idle
timeout), which failed one Guided run; and the local-model adapter used Ollama's default context window, which silently
cut long planner prompts.

Rows measured before these fixes are kept in `artifacts/benchmark-results.v0.jsonl` and are not quoted.

## 4. Known issues and limits

- Guided's planner sometimes plans only a few intents and success checks (two or three); the hotel's date rules were
  missing in some hosted-model runs, so past and reversed dates stayed AI suspicions instead of proven findings.
- Driver models still sometimes describe their own tool use as a bug; these are filtered and noted, but cost calls.
- Probes reach only endpoints the UI triggered at least once (OpenAPI or HAR import would remove this).
- AI-written rules can be wrong; the engine labels rules broken on most responses, but they still count as false
  positives in the benchmark.
- The "rule may be wrong" label is a heuristic (broken on nearly every response). It also lands on correct rules when
  a bug shows on every response: in the hotel Hybrid run, `total == nights * rate` was right but broken on every
  booking, because every stay the hunt made was three nights or more.
- Shrinking stops at a 10-minute budget; long repros (overselling) shrink only partly.
- Hunts run inside the API process with secrets in memory; they do not resume after a restart.
- Both demo apps were written by the same author as the hunter (see the threats to validity in evaluation.md).

## 5. Next

1. Run the benchmark matrix across additional hosted models under the corrected scoring, several runs each.

2. The head-to-head with a code-generation agent and Playwright, using the protocol in evaluation.md.
3. Guided: have the planner cover each story's acceptance criteria with at least one success check.
4. Metamorphic testing from the app map (two routes to the same state must agree).
5. Production hardening: authorized targets, probe rate limits, hunts on a job queue, step-level storage.

## 6. How to demo

A five-minute walkthrough:

1. Start the two demo apps, the API and the UI (README, "Quick start").
2. **New hunt → Use Demo Hotel**, strategy **Heuristic** (no AI). Show the hunt booking a stay with past, reversed
   dates and -1 guests, the hotel accepting it, and Bug Hunter reporting nothing: evidence without rules is not a bug.
3. Same app, strategy **Guided** with a model. Show the rules written from stories HOTEL-1 to HOTEL-4, the broken ones
   as findings with "Reproduced 2/2", the recorded evidence and the Playwright test.
4. **Benchmarks**: the same configurations with every defect off, and the false-positive columns.
5. Export the hunt as SARIF or JUnit and show it failing a pipeline only on confirmed findings.
