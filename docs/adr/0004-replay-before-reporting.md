# ADR 0004: Every finding is replayed without AI before it counts

Status: Accepted

## Context

A finding that cannot be reproduced is not actionable, and an AI-driven run cannot be trusted to have recorded what it claims.

## Decision

After the hunt, each proven finding is replayed twice in fresh browser sessions with no model: elements are found again by role and name (numbers in labels ignored), and probe steps resend the call recorded in the new session. Confirmed means both replays reproduced it. A setup hook resets test data before every replay. Confirmed repros are shrunk by delta debugging over whole user actions.

## Alternatives considered

- Report findings directly: rejected; no way to separate real bugs from noise.
- Re-run the generated Playwright spec: kept as output, but the replayer gives a verdict without a test toolchain.

## Evidence

Without the setup hook only 2 of 7 findings reproduced on the second replay (the first replay changed the shop). With it: 7 of 7, and repros shrank from 326 to 133 steps.
