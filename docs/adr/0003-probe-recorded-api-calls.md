# ADR 0003: Probe the API calls the app really makes, through the browser session

Status: Accepted

## Context

Finding a bug by clicking depends on the explorer choosing the right inputs and reaching the right page, which small models often fail to do. Custom widgets also make locators unreliable.

## Decision

After exploring, the engine resends each recorded API call with risky changes (boundary values, invalid formats, repeats, stock + 1 then an order, other letter case, no session) through the browser context with the app's own headers. Responses are judged by the same oracles and rules. Probes stop when the session ends rather than report results that prove nothing.

## Alternatives considered

- Fuzzing from an OpenAPI spec: useful later (import), but many apps have no spec.
- UI-only testing: misses server-side validation gaps the UI hides.

## Evidence

Probes found overselling, which the UI sweep missed, and a crash nobody seeded (`qty = 0.5` returns HTTP 500).
