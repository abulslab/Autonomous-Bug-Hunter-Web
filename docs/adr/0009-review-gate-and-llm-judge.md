# ADR 0009: A review gate, with an LLM judge that has to earn the right to decide

Status: Accepted

## Context

Models draft rules from stories, hunts mine journeys, and runs record behaviour that changed. If all of that became an
expectation by itself, the system would learn its own mistakes: a wrong rule fails builds forever, and a bug the app
started doing would be accepted as the new normal. Asking a person about everything does not scale either.

## Decision

Nothing a model proposed and nothing the app started doing becomes an expectation without a decision:

- The queue holds AI-drafted rules, proven journeys (before they gate builds), behaviour changes no story explains, and
  baseline updates that are not pure improvements.
- People decide in the UI. Every decision is logged with who made it and why, and deciding again reverses it.
- An LLM judge can be configured per project. It always gives a verdict with a reason, but it decides only when:
  bypass is on; it is from another model family than the planner that drafted the proposal; it has been compared with
  at least N people's decisions; and its measured agreement with them is at or above a threshold.
- A share of the judge's decisions is sampled (deterministically, by item id) for a person to spot-check.
- Behaviour changes, and baseline updates with new findings or regressions, always need a person: turning what the app
  does into what it should do is the one decision principle 2 never delegates.
- Baselines advance by themselves when a run brings only improvements; otherwise the update is a review item.

## Alternatives considered

- Human review only: correct, but the queue grows with every run and gets rubber-stamped.
- Judge always on: cheap, but unmeasured, and a model grading its own family's work agrees with itself.

## Evidence

Unit tests cover each condition of the gate. Agreement is measured from live decisions; the judge's agreement with the
answer keys is part of the final benchmark.
