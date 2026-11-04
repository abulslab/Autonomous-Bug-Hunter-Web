# ADR 0002: AI-written expectations are rules in a small language, not code

Status: Accepted

## Context

Acceptance criteria say what should be true ("the total is subtotal - discount + shipping + tax"). To check them on every response the expectation has to be executable, but running model-written code is unsafe and hard to validate.

## Decision

Models write rules in a small expression language (`Invariants.cs`): field paths, arithmetic, comparisons, list functions. A rule is admitted only if it parses, targets an endpoint the hunt has seen, and evaluates on at least one recorded response. Evaluation is three-valued: a missing field makes a rule not applicable, never failed.

## Alternatives considered

- Generated JavaScript or C#: rejected (sandboxing, non-determinism).
- Natural-language checks judged by a model: rejected (ADR 0001).

## Evidence

Admission caught guessed endpoints copied from prompt examples and invented field names. A rule with a precedence mistake fired on 11 of 12 correct carts; rules broken on most responses are now marked as probably wrong.
