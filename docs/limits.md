# Limits

What Bug Hunter does not do, or does not do yet, and what has and has not been tried. It is measured on the two demo
apps in this repository and their seeded defects; it has not been measured on a large production application. Read
the boundaries below as "designed for" unless they say "measured".

## 1. Summary

| Area | Handled | Not handled | What to do instead |
|---|---|---|---|
| Network | Requests the page makes with `fetch` and `XMLHttpRequest`: JSON and form bodies, status codes, headers | WebSocket and server-sent-event streams, binary protocols | Check streaming services with their own tests |
| Sign-in | Form sign-in with test accounts held as project secrets; cookie and bearer sessions | Interactive multi-factor prompts, captchas, sign-in pop-ups | Test accounts with multi-factor sign-in turned off in a test environment |
| UI | Pages with a DOM and an accessibility tree; elements found by role and name | Interfaces drawn on a canvas, closed shadow roots | Expose accessible elements, or test the API directly |
| Test data | A reset hook called before the hunt and before each replay (measured on both demo apps); optional plus-addressed test users | Shared state that a reset cannot restore, such as the last unit of a single global stock | A resettable test environment |
| Expectations | Rules that can be checked on data: totals, statuses, state transitions, permissions | Subjective qualities ("looks right", "feels slow") | Human review |

## 2. Findings and replays

- **A confirmed finding reproduced; that does not make the expectation right.** A finding is confirmed only when a
  deterministic oracle, rule or prediction fired and two replays without AI reproduced it. A wrong AI-written rule
  reproduces as faithfully as a real bug. This is why AI-written rules count towards readiness only after review,
  rules broken on most responses are flagged as probably wrong, and false positives are measured with every seeded
  defect off. The reference run found and fixed one such case ([reference-run.md](reference-run.md)).
- **Replays depend on resettable test data.** Without a reset hook, a replay starts from whatever the hunt left (a
  coupon already used, stock already sold), and a real bug can come out as flaky or not reproduced. Measured: without
  the hook, 2 of 7 findings reproduced on the second replay; with it, 7 of 7 ([ADR 0004](adr/0004-replay-before-reporting.md)).
- **Plus-addressed test users are opt-in.** `DynamicUsers` rewrites a test account's email per replay
  (`name+replay1@...`). It helps only where such accounts can sign up or already exist, so it is off by default.
- **Shrinking has a time budget.** A long repro may stay long.

## 3. Exploration

- **Probes reach the endpoints the UI called or a specification declared.** An endpoint no page calls and no imported
  OpenAPI document or HAR file describes is not probed.
- **New request fields do not yet steer a change-driven hunt.** The twin records them, but the hunt may not try them:
  the reference run missed a bug behind a new option for this reason.
- **Journeys recorded through long UI paths are brittle.** A change to a page on the way, such as a sign-in page,
  can make a proven journey fail.
- **AI-drafted lifecycles are not reviewed yet.** A wrong lifecycle from a model makes its lifecycle prediction fire
  on correct behaviour; lifecycles should pass the review gate like rules.

## 4. Models

- **Results depend on the model.** A small local model on a CPU is slow and writes fewer correct rules; the harness
  reports configurations by strategy so a team can measure the model it brings.
- **Rate limits and failures.** When a provider rate-limits or fails repeatedly, the hunt continues with the no-AI
  explorer and probes and records what it could not do. A run made that way is weaker, not wrong.
- **The LLM judge agrees too readily.** In the reference run it accepted every item it was given. It stays advisory
  until its measured agreement with people is above the project's threshold.

## 5. Operations

- **Hunts run inside the API process.** They do not resume after a restart; secrets are held in memory by design.
- **Integrations are simulated in this repository.** Jira and Confluence adapters are written against the services'
  REST shapes and tested against the simulators in `src/Simulators`, not yet against the hosted services.
- **Scale is untested.** The document store has a PostgreSQL backend for several API instances; concurrency and
  large applications have not been measured.

## 6. Non-goals

- Pixel-level visual regression.
- Load and performance testing.
- Changing application code: Bug Hunter files bugs, writes reproducing tests and exports SARIF; people fix.
