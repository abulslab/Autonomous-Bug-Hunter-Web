# ADR 0010: Release readiness is computed from proven evidence, and written back to the team's tools

Status: Accepted

## Context

A QE lead's question before a release is not "how many tests passed" but "can we ship, and what are we shipping with".
A report that a model writes would be persuasive and unverifiable. A gate that lives only in Bug Hunter's UI is not
part of the team's workflow.

## Decision

- The readiness verdict is a pure function of proven state: the latest run's findings (confirmed by replay, minus
  triage suppressions), journeys proven by replay, the comparison with the baseline, the review queue and the change
  not yet hunted. No model is involved.
  - **Not ready**: no finished run; a confirmed bug open that is not a triaged known issue; a journey regressed since
    the baseline; a blocking review item undecided.
  - **At risk**: unconfirmed findings, known issues shipping, changed stories not hunted, reviews waiting, story
    coverage under 80%, unexplained behaviour changes, flaky or failing journeys.
  - **Ready**: none of the above.
- The gate runs from CI (`scripts/ci-gate.mjs`): sync, hunt what changed first (or survey every role), wait for the
  run, read the verdict, fail the job when not ready.
- Results go where the team works, behind the same ports as the knowledge sources: findings become Jira bugs linked to
  the stories they break (once per bug, then comments), and the report is published as a versioned Confluence page in
  its own space, so a report never becomes knowledge.

## Alternatives considered

- A model-written summary: readable, not auditable.
- Pass/fail on findings only: ignores regressions, coverage and what was never tested.

## Evidence

Unit tests cover each verdict rule and the storage conversion; `scripts/workflow-demo.mjs` runs the sprint workflow end
to end against the simulators.
