# ADR 0005: Two model roles: a planner for few calls, a driver for many

Status: Accepted

## Context

Reasoning quality matters most in a few places (modelling the app, writing intents and rules), while choosing the next click needs many short calls. One strong model everywhere is expensive; one cheap model everywhere is weak.

## Decision

A hunt has a driver model (each step) and an optional planner model (warm-up, rules, probe ideas). Every call goes through one path that enforces the hunt's AI budget, a deadline, usage accounting and cut-off detection; past the budget the hunt finishes without AI. Model calls are counted per hunt.

## Alternatives considered

- A single model: simpler, but forces a cost and quality trade-off everywhere.
- A fixed vendor: rejected; any provider behind the gateway, or a local model, can fill either role.

## Evidence

A small local model running on CPU capped every AI result (a 4k context window silently cut prompts). A hosted model's thinking tokens consumed the output limit and cut JSON replies until the gateway added headroom and a retry; afterwards a Guided hunt made 7 calls with no repairs.
