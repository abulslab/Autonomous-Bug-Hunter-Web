# Skill packs

A skill pack is a short Markdown file of domain testing knowledge: the risks a careful tester would look for, rules that
must hold, and probe values worth trying. During warm-up Bug Hunter picks the packs whose `match` keywords appear in the
application it discovered (page text, element names, API paths) and gives them to the planner model, alongside your user
stories. The planner turns them into intents, rules and probes; the engine still decides what is a bug.

Packs are plain files so a team can review, version and extend them like code, without changing Bug Hunter.

## Format

```markdown
---
name: payments                 # unique id
title: Card payments           # shown in reports
match: card, payment, refund   # any of these in the app selects the pack
---

## Risks
- One risk per line, specific enough to test.

## Rules
- Checks in Bug Hunter's rule language, with where they apply.

## Probe values
- Values worth trying, by field.
```

Rules here are suggestions to the planner; every rule is validated against recorded traffic before it is used.
