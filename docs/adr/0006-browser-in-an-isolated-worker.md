# ADR 0006: The browser runs in an isolated worker process

Status: Accepted

## Context

A real browser executes untrusted pages, uses a lot of memory and crashes in its own ways. It must never hold model keys, and secrets typed into the app must never reach a model.

## Decision

The browser lives in a separate Node + Playwright worker that the API drives over a small HTTP protocol. The worker enforces a per-session host allowlist, substitutes `{{secret:name}}` references itself and masks secret values in snapshots, network evidence, screenshots, the live stream and recordings.

## Alternatives considered

- Browser inside the API process: rejected (stability, isolation, scaling).
- A hosted browser grid: possible later behind the same protocol.

## Evidence

Secrets were visible in the live stream until masking moved into the worker and was applied before typing.
