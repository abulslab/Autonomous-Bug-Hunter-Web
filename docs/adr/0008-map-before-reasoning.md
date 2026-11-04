# ADR 0008: Map the app before reasoning about it (warm-up, then intents)

Status: Accepted

## Context

Letting a model observe, reason and act on every step ("self-driving without a map") is slow, costly and drifts: models signed out, repeated the same action and never reached checkout.

## Decision

A Guided hunt first surveys the app with no AI, building a map of states and roads and a catalog of the API. The planner then writes the app model and intents (what users come to do, success checks, risky variants), grounded in stories, product notes and skill packs. A navigator drives known routes to each intent; the driver model only handles the last mile. The map is kept per site, so later hunts survey less.

## Alternatives considered

- Per-step reasoning everywhere: measured to drift and overspend.
- Fixed scripted journeys: no discovery.

## Evidence

Map reuse cut the survey from 17 to 10 steps. An early strategist matched states by partial name and sent every destination to the home page; states are now numbered and matched exactly.
