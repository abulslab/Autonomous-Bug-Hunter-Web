# Changelog

All notable changes. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added
- **P0, ground truth for a continuous system** ([plan](docs/plan-continuous-qe.md)).
  - Both demo apps publish an OpenAPI document (`/openapi/v1.json`) of their product API; test-harness routes are
    left out of it.
  - A second role in each app: shop staff with an order desk (Processing → Shipped → Delivered; shoppers cancel
    while Processing), and a hotel front desk (check-in, check-out; guests cancel while Confirmed). Hotel guests are
    now scoped to their browser instead of every guest seeing every booking.
  - Four new seeded defects per app: permission (order desk or front desk open), lifecycle (cancel after shipping or
    check-in), consistency (listed price or quoted rate differs from what is charged), and one inside a new feature.
  - Feature switches (gift wrap, breakfast) and versioned stories editable through the harness.
  - Answer keys and sprint-2 scenarios under `ground-truth/`; `scripts/scenario.mjs` applies them.
  - Tests: answer keys match each app's OpenAPI document; roles, lifecycles, features and the new defects behave as
    specified; the scoring recognises the new defects.

- **P1, projects and knowledge.**
  - Projects (`/api/projects`): environments with reset hooks, roles mapped to write-only project secrets,
    knowledge sources, skill packs always applied, out-of-scope areas. The demo apps are created as projects on a
    fresh install.
  - Knowledge sync: stories (files or Jira) and documents (files or Confluence) become a versioned snapshot; a new
    version only when something changed, with a criterion-by-criterion diff. Hunts started from a project record
    the knowledge version, each story's version and source, the environment and the role.
  - `src/Simulators`: Jira Cloud (search, issues with ADF, changelog, create, transitions, comments) and Confluence
    Cloud (spaces, versioned pages in storage format with optimistic locking) simulators, seeded from
    `ground-truth/<app>/knowledge/`. Scenario story edits also go to the Jira simulator.
  - `node scripts/dev.mjs` starts every app with one command.
  - UI: Projects list and project page (sources, stories with versions, documents, history with diffs, hunts,
    start a hunt).
  - Tests: simulator contracts, the Jira and Confluence adapters against the simulators, knowledge diffs, the
    knowledge files equal to the stories each app serves, the project API.

- **P2, app twin foundation.**
  - Every project hunt adds a twin version: pages, routes and API endpoints with evidence, status, first and last
    version seen, roles; statuses per role (calls an engine probe sent without a session are tagged no-session);
    request and response fields; changes against the previous version; unknowns (roles never explored, endpoints
    that never succeeded, writes never tried without a session).
  - `GET /api/projects/{id}/twin` and `/twin/history`; an App model tab on the project page.
  - `scripts/twin-score.mjs` scores a twin against the app's answer key; `scripts/project-hunt.mjs` runs a project
    hunt from the command line. BENCHMARKS.md gains an app twin accuracy table.

- **P3, specifications and roles.**
  - OpenAPI 3 import (`POST /api/projects/{id}/specs/import`): every operation becomes a declared twin endpoint with
    its documented statuses and request and response fields ($refs resolved). A HAR file sent to the same endpoint
    adds its API calls as observations. The demo projects import their apps' own `/openapi/v1.json`.
  - Drift as unknowns: documented but never called, called but undocumented, fields returned but not documented,
    fields documented but never returned.
  - Role survey (`POST /api/projects/{id}/survey`): one no-AI hunt per role, each waiting for the one before (the
    runner now chains hunts), so their test-data resets do not collide.
  - Permission matrix (`GET /api/projects/{id}/permissions`): endpoint by role, allowed, denied, error or unknown;
    shown on the App model tab with Import specs and Survey all roles buttons.
  - `twin-score.mjs` compares the matrix with the answer keys. Two answer-key mistakes it found were corrected.

- **P4, AI understanding with a verifier.**
  - `POST /api/projects/{id}/understand` (a model must be named; no provider default is used): one model call
    proposes entities, features, lifecycles and rules from the twin, the knowledge and the skill packs.
  - The verifier keeps an entity only with fields the API has, a feature only with existing pages or endpoints, a
    lifecycle only with transitions on known endpoints, a rule only if it parses, targets a known endpoint and uses
    its fields. Kept items are inferred, or verified when all they rest on was observed; refusals are kept with the
    reason. The same input is not sent twice.
  - The App model tab shows entities, features, lifecycles, rules, refusals and the model run (time, tokens, cost);
    local models are listed first.
  - `twin-score.mjs` scores the understanding layers against the answer keys.

- **P5, journeys.**
  - After each project hunt, the outcomes it reached (successful writes other than sign-in and the harness) become
    candidate journeys: the shortest path per role and outcome, linked to the twin's features and stories.
  - The role's unproven journeys are replayed twice in fresh sessions without a model, after the reset hook:
    proven (2 / 2, with a Playwright test), flaky or failing; each run is kept.
  - Planned journeys: twin features that write something and that no journey reaches yet.
  - `GET /api/projects/{id}/journeys` and `/coverage` (per story: features, rules, proven journeys); Journeys and
    Coverage tabs on the project page; journey scoring in `twin-score.mjs` and BENCHMARKS.md.

- **P6, predictive twin.**
  - Predictions on every project-hunt response: lifecycle (from the twin), permission (project roles declare their
    own areas, `exclusive`), consistency (listed price or rate equals the charged one). The twin's rules join the
    hunt's rules.
  - Probes: the same write for other listed items, another role's endpoints with this role's session (cross-role
    replay), a transition on an entity in a state it should not leave that way. Write probes run only where
    irreversible actions are allowed.
  - Twin-mismatch findings (`Prediction` provenance) are confirmed by replay; the benchmark credits a seeded defect
    to the prediction kind that is about it and counts unexplained prediction findings as false positives.
  - Benchmark row `survey-120`: one no-AI project hunt per role, combined.

- **P10, workflow integration and release readiness.**
  - `GET /api/projects/{id}/readiness` (JSON, or `?format=markdown`): the verdict (ready, at risk, not ready) from
    proven state, with blockers, risks, open bugs, story coverage, journeys and changes since the baseline.
  - `POST .../readiness/publish`: the report as a versioned Confluence page in its own space (`PUT .../workflow` sets it).
  - `POST .../findings/{hunt}/{finding}/jira`: a Jira bug with repro steps, linked to the story it breaks; once per
    bug, later sightings comment. "File in Jira" on project findings.
  - Simulators: Jira issue links; Confluence space and page creation.
  - `scripts/ci-gate.mjs` (the release gate for CI) with `docs/examples/bug-hunter-gate.yml`, and
    `scripts/workflow-demo.mjs` (sprint 1, triage, sprint 2, release). A Readiness tab. ADR 0010.

- **P9, change-driven hunting and learning.**
  - A sync that changes stories applies their impact: rules of changed stories go stale until rewritten and accepted
    again; journeys that cite the stories or end at an affected endpoint are proven again.
  - `GET /api/projects/{id}/impact`; `POST .../hunts/changed` tests the change first (changed stories, affected
    endpoints, areas new in the twin since the baseline, priority endpoints).
  - `POST .../triage` (bug, not-a-bug, known-issue, wont-fix), kept by finding key: suppressed findings are left out
    of run snapshots; bugs make their endpoint a priority in later missions. Triage on a project hunt's findings.
  - `GET .../learning`: rule library with hold and violation counts, stale and retired rules, triage, priorities.
  - A "Changes & learning" tab.

- **P8, review gate with an LLM-judge bypass.**
  - `GET /api/projects/{id}/review`: the queue (AI-drafted rules, proven journeys, unexplained behaviour changes,
    baseline updates); `POST .../review/{item}` decides (and reverses); `PUT .../review/settings`; `POST .../review/judge`.
  - The judge must be another model family than the planner, and decides only with bypass on, at least N decisions
    compared with people and agreement at or above the threshold; a share of its decisions is spot-checked. Behaviour
    changes and baseline updates with new findings or regressions always need a person.
  - Rejected rules retire (never proposed again); rejected journeys are not replayed; accepted behaviour changes are no
    longer change signals. Runs that bring only improvements advance the baseline by themselves.
  - A Review tab. ADR 0009.

- **P7, runs, baselines and run comparison.**
  - Project hunts and surveys are recorded as runs; a finished run's snapshot holds proven findings, journey
    statuses, endpoint behaviour fingerprints, story versions and covered stories.
  - `GET /api/projects/{id}/runs/{run}`: the comparison with the baseline (new, fixed, still open; regressed,
    recovered and new journeys; behaviour changed with the stories that explain it, if any; endpoints added and
    removed; stories changed; coverage gained and lost). `POST .../accept` makes a run the baseline; the first finished
    run becomes it automatically, in start order.
  - Proven journeys are replayed in every hunt of their role (the regression suite); a proven journey that fails is
    noted as a regression.
  - A Runs tab with "What changed since the baseline".

### Changed
- A cross-endpoint seeded defect (quoted rate, listed price) is credited only to a consistency prediction, not to a
  rule on one response.
- Forms are swept one invalid value at a time with every other field plausible, then with every field plausible;
  every value list ends with its plausible value (names, emails, card numbers and ZIP codes did not, so the
  "valid" round was not valid).
- "Every defect on" leaves a defect that lives inside a feature off while the feature is off.
- Benchmark rows on the earlier ground truth are archived (`artifacts/benchmark-results.v1.jsonl`).
- **Portfolio review fixes** (found by two end-to-end sprints from an empty store):
  - `prev` in rules is the same entity as it was last seen (on a path with an id, from any earlier response that
    carried that id), not the previous call to the endpoint. Lifecycle rules such as "checked in before checked out"
    compared one booking with another and failed on correct behaviour, faithfully on every replay.
  - A rule the model writes again in other words (spacing, quotes, outer parentheses, the `status >= 400 ||` guard)
    replaces the earlier wording; stale rules of a changed story are replaced by the rewritten ones.
  - Runs record where each finding shows (endpoint and role) and what they exercised: a bug gone from a run that never
    went back there is "not retested", not "fixed".
  - Release readiness covers every run since the baseline, not only the latest; baseline bugs not retested stay open.
  - Findings share a root cause (a role reaching a protected area is one bug however many endpoints show it): one
    Jira issue per cause, readiness counts causes.
  - Story coverage counts only accepted rules and proven journeys; older baseline proposals and rules no longer in the
    twin leave the review queue.
  - Skill packs are served by the API (`GET/PUT/DELETE /api/skills`): built-in packs from `skills/`, custom packs kept
    in the data directory and used by every hunt. The Grounding page shows each project's real knowledge.
  - Dynamic plus-addressed test users are opt-in (they rewrite existing accounts into ones that may not exist).
  - The benchmark harness takes the model as a parameter (`--model`) and reports configurations by strategy, with no
    model names or prices; README and docs make no model or price claims.

## [0.2.0] - 2026-04-11

### Added
- **Ground truth.** Demo Hotel (`src/DemoHotel`), a second benchmark app whose four seeded defects are business rules
  no generic oracle can see. A benchmark harness (`scripts/benchmark.mjs`) runs every configuration on both apps with
  defects on and off and generates `docs/BENCHMARKS.md`. The evaluation method, including the head-to-head protocol,
  is in `docs/evaluation.md`.
- **Reports and integrations.** Export a hunt as Markdown, self-contained HTML, SARIF 2.1.0 (code scanning) or JUnit
  (any CI test view). File a finding as a GitHub issue; a finding seen again comments on its open issue instead of
  opening a duplicate.
- **Warm-up and intents (Guided, experimental).** A survey without AI, an API catalog, skill packs (`skills/*.md`) and
  an app map; the planner turns stories into user intents with success criteria and variants; the engine navigates to
  each one and records whether it held.
- **Operational baseline.** OpenAPI with a reference UI at `/docs`; `/healthz` and `/readyz`; optional API key; rate
  limiting on starting hunts; OpenTelemetry traces and metrics; a PostgreSQL document store; Docker Compose with
  optional Postgres and Jaeger; CI with a real smoke hunt.
- **Cost controls.** A per-hunt AI budget, separate planner and driver models, token and cost accounting per call, and
  cost per confirmed bug.
- A new UI: white, quiet and dense, with hunt overview, launch form, findings with evidence and replay status, intents,
  rules, timeline, coverage, app maps, benchmarks and model checks.
- Documentation: architecture (C4), eight ADRs, security, configuration, extending, contributing.
- `scripts/screenshots.mjs` captures the README screenshots from a running UI.

### Changed
- Separate planner and driver model selections for benchmarks and hunts.
- Strategist answers are matched to listed states exactly; a partial match could send every destination home.
- Benchmark model rows are opt-in by family (`--models local,gemini,claude`); a plain run uses no model.

### Fixed
- Rules for an endpoint the survey had not reached yet were rejected at planning time, so a booking rule written
  from the stories was thrown away before the first booking. They now wait for the endpoint, under the API the app
  already uses, and are checked from its first response.
- Benchmark scoring credited a broken rule to every seeded defect its response happened to show; a rule now earns
  credit only for defects whose fields it checks. False positives from AI-written rules are now counted (they were
  left out of the false-positive table).
- The sweep never ended a form on a valid value (the plausible value was past the round limit), so a boundary value
  could leave the app on an empty page; it also had no notion of date fields.
- The browser worker snapshotted single-page apps before same-document navigations had fetched and rendered.
- A hunt waiting on slow model calls lost its browser session to the worker's ten-minute idle timeout; the engine now
  keeps its session alive.
- Local models ran with Ollama's small default context window, which silently cut long planner prompts; the adapter
  now uses Ollama's native API with a configurable window (`Models:OllamaContextTokens`, default 16384).
- Findings from API probes showed no screenshot; they now show the last page before the probe.
- Gemini replies cut off by thinking tokens: detected, retried once with more room, and thinking effort set low.
- Ollama selections were sent to the OpenAI-compatible adapter (HTTP 404).
- Replays missed elements whose labels contain counts ("Cart (0)").
- The model could report its own plan as a finding, or sign the test user out mid-hunt.

## [0.1.0] - 2026-04-11

### Added
- Standalone quality-engineering system: hunt engine,
  oracles, rules from requirements, engine probes, replay confirmation and repro shrinking, browser worker, demo shop.
