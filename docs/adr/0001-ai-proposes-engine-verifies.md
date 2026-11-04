# ADR 0001: The AI never decides what is a bug

Status: Accepted

## Context

An autonomous tester that lets a model judge whether something is a bug produces confident fake bugs, and its results cannot be reproduced. Code-generation agents have the opposite problem: they assert what the app does today, so they can lock a bug in as the expected value (the test-oracle problem).

## Decision

A finding counts as proven only when deterministic code fires on recorded evidence: a built-in oracle, or a rule checked by the engine. Model opinions are kept separately as *suspicions* that need triage, and suspicions about the hunter's own tools are discarded.

## Alternatives considered

- LLM as judge (an "oracle agent"): rejected; unverifiable and not reproducible.
- No AI at all: kept as a strategy (Heuristic), but it cannot read requirements or plan.

## Evidence

In measured runs, model suspicions included a genuine bug and also "incorrect routine argument usage" (the model reporting its own mistake). With every seeded defect switched off, the engine reported 0 findings.
