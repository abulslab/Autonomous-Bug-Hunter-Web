# Reference run: two sprints on Demo Hotel, from an empty store

What the system does across two releases of an application it has never seen, run end to end by
`node scripts/scenario.mjs` and the project API, with every hunt, map, twin and benchmark result deleted first. The
scenario is `ground-truth/hotel/scenarios/sprint-2.json`; the counts below are against its seeded defects.

The run was made twice: once with a hosted model for understanding and AI hunting (before the fixes below), and once
after the fixes with a small local model for understanding and no AI at hunt time. Models are not named: results depend
on the model a team brings, and the point is what the engine proves with it.

## The scenario

| | Sprint 1 | Sprint 2 |
|---|---|---|
| Story change | none | HOTEL-3 "Correct price" gains breakfast: two new acceptance criteria |
| Seeded defects on | room capacity ignored, last night not charged, front desk open to guests | capacity and front desk still open; **new** in the changed story: breakfast charged once; **regression** in an area no story touched: charged rate differs from the quote; **fixed**: last night not charged |

## Sprint 1: first contact

| Stage | What happened |
|---|---|
| Knowledge | 6 stories and 3 documents synced from the Jira and Confluence simulators as knowledge version 1 |
| Specification | 10 endpoints declared from the app's OpenAPI document (twin version 1) |
| Understanding | The hosted model proposed 14 items (3 entities, 4 features, the booking lifecycle, 6 rules from HOTEL-1 to HOTEL-5) in one call; the verifier kept all 14. The local model proposed 13 and the verifier refused 2 |
| Survey, every role, no AI | The front desk open to guests found by the permission prediction, replayed 2/2, on three endpoints |
| AI hunt (Hybrid) | **3 of 3** seeded defects found and confirmed: capacity and the missing night by rules written from the stories, the front desk by a prediction. The provider rate-limited part of the hunt; it continued without AI for those steps |
| Review | 15 items waiting: 6 AI rules, 5 journeys, 3 behaviour changes, 1 baseline update |
| Judge (advisory) | Accepted all 12 items it was given, including journeys that only work because of the front-desk defect. Bypass was off, so nothing was applied: this is why bypass needs measured agreement first |
| Workflow | Confirmed bugs filed in Jira, the readiness report published to Confluence |
| Verdict | **Not ready**: confirmed bugs open |

## Sprint 2: the change

| Stage | What happened |
|---|---|
| Knowledge | HOTEL-3 v1 -> v2 detected criterion by criterion |
| Impact | Traced to the "Book a stay" feature, `POST /api/bookings`, the HOTEL-3 rule (made stale) and the booking journey (to be proven again) |
| Understanding | Rules rewritten from the new criteria, including a breakfast total rule |
| Change-driven hunt | Mission: the changed story first, the affected endpoint, the endpoints where real bugs were found before. **3 of 4** open defects found: capacity, front desk, and the quote regression (by the consistency prediction, with no AI). The new breakfast defect was **missed**: the hunt never chose the breakfast option |
| Comparison | Story change shown; behaviour changes marked as explained by HOTEL-3 or not |
| Verdict | **Not ready** |

## What the run found in Bug Hunter itself, and what changed

The first run was a test of the system as much as of the hotel. These defects were found by it and fixed; each fix
has a unit test.

| Found | Effect | Fix |
|---|---|---|
| `prev` in a rule meant "the previous call to this endpoint", which for `/bookings/{id}/checkout` was another booking's checkout | A rule "checked in before checked out" failed on correct behaviour, and replay reproduced the false positive faithfully: 1 in sprint 1, 2 in sprint 2 | `prev` is the same entity as it was last seen, from any earlier response that carried its id. After the fix: 0 rule false positives in both sprints |
| Readiness looked only at the latest run | Sprint 2's report missed the regression and the capacity bug found by the change-driven hunt | Readiness covers every run since the baseline |
| A bug absent from a run that never went back to where it showed was counted as fixed | Two open front-desk bugs reported as fixed by a guest-only hunt | Runs record what they exercised; such bugs are "not retested" and stay open |
| A run that retested less could become the baseline by itself | After the fix above, a guest-only hunt replaced a two-role survey as the baseline | A run with bugs not retested never advances the baseline by itself |
| A rule rewritten in other words was added beside the old one | One defect, two findings | Equivalent checks are one rule; stale rules are replaced |
| One leak, three endpoints, three bugs | Three Jira issues for one defect | Findings share a root cause; one issue per cause; readiness counts causes |
| Coverage counted AI rules nobody had reviewed | 6 of 6 stories "covered" before any review | Only accepted rules and proven journeys count (1 of 6 after the fix, before review) |
| Older baseline proposals stayed in the queue | 26 review items after two sprints | Superseded proposals leave the queue (11 after the fix) |

## Still open

- **New fields do not steer the change-driven hunt yet.** The breakfast option was a new request field; the twin
  learned it, but the hunt did not try it. Re-importing the specification on every deploy, and listing new fields as
  new areas, is the next step.
- **An AI-drafted lifecycle can be wrong.** The local model's booking lifecycle forbade checking out from CheckedIn;
  the lifecycle prediction then fired on correct behaviour. Lifecycles should pass the review gate like rules before
  their predictions can block a release.
- **Journeys recorded through long UI paths are brittle.** Proven journeys through the front-desk sign-in can break
  when a defect changes that page.
- **The judge agrees too readily.** That is measured, not hidden: it stays advisory until its agreement with people
  is high enough.
