# Extending Bug Hunter

Five extension points, from cheapest to most involved. Only the first two need no code, and the first is where most
teams should start.

| To add | Edit | Code? | Reviewed by |
|---|---|---|---|
| Rules for your app | The hunt's stories or **Rules you write yourself** | No | Anyone who reads the acceptance criteria |
| Domain knowledge | A skill pack in `skills/` | No | Your QA lead |
| A new kind of bug the engine can prove | An oracle in `Hunter/Oracles.cs` | C# | A developer, with a unit test |
| A new way to attack a page | A routine in `Hunter/Routines.cs` | C# | A developer |
| A new model provider | An `IModelProvider` in `Models/` | C# | A developer |

## 1. Rules

A rule is one line: the endpoint it applies to, a check, and a comment saying what it means.

```
POST /api/bookings: status >= 400 || total == nights * rate   # every night is charged
GET /api/cart: total == subtotal - discount + shipping + tax     # the cart adds up
POST /api/cart/items: status >= 400 || request.qty >= 1          # quantities below 1 are rejected
GET /api/products: all(items, price > 0)                         # nothing is free by accident
```

The check language has response fields (`total`, `items[0].price`), `request.x` (what the browser sent), `status`,
`query.x` and `prev.x` (the previous matching response); arithmetic, comparisons and `&&` `||` `!`; and the functions
`sum`, `count`, `all`, `any`, `min`, `max`, `abs`, `round`, `len`, `lower`, `contains`, `exists`, `date` and `today`.
A check that needs a field the response lacks is skipped for that response, not failed. The engine evaluates every rule on every recorded response, and
backfills rules added mid-hunt over what was already recorded. When a hunt is given user stories, the planner model
drafts rules like these from the acceptance criteria; a drafted rule is admitted only if it parses, names an endpoint
the hunt actually saw, and does not break on most of the recorded responses (a rule that fails everywhere is wrong,
not a bug). See [ADR 0002](adr/0002-rules-in-a-checkable-language.md).

## 2. Skill packs

A skill pack is a Markdown file in `skills/` that tells the planner what usually goes wrong in a domain. During
warm-up, packs whose `match` keywords appear at least twice in what the survey found are given to the planner.

```markdown
---
name: bookings
title: Reservations and bookings
match: booking, reservation, check-in, stay, night, guests, room
---

## Risks
- Date order: the end of a stay comes after its start; a stay is at least one night.

## Rules
- On booking writes: `status >= 400 || total == nights * rate`

## Probe values
- Dates: yesterday, check-out equal to check-in
```

Packs are guidance, not answer keys: rules in a pack still go through the same admission checks as any drafted rule.
Keep them about bug classes of the domain, not about one application. The shipped packs (`ecommerce`,
`authentication`, `search-and-lists`, `bookings`) are examples to copy.

## 3. Oracles

An oracle turns recorded evidence into a proven finding with no AI. Add it to `Oracles.Network` (one HTTP response) or
`Oracles.Evaluate` (the page snapshot) and return a `Hit`:

```csharp
// A refund larger than the order it refunds.
if (ok && n.Path.Contains("/refunds") && InvariantRules.Number(body?["amount"]) is decimal amt && InvariantRules.Number(body?["orderTotal"]) is decimal tot && amt > tot)
    hits.Add(new($"refund-exceeds-order:{n.Path}", "refund-exceeds-order", "Refund exceeds the order total", "High",
        "A refund is never larger than the order.", $"amount {amt} > orderTotal {tot}", Trim(n.Body), n.Method, n.Path));
```

Rules of thumb: an oracle must be right on any application, not only yours (otherwise it is a rule); the `Key` decides
deduplication and the replay match, so include what makes two hits the same bug; add a unit test with a recorded
response that should fire and one that should not. False positives from an oracle cost more than the bugs it finds.

## 4. Routines

A routine is a scripted attack the model can choose by name, such as `boundary-values` or `repeat-submit`. Implement
`IRoutine` (`Name`, a one-line `Usage` for the prompt, and `RunAsync` using `RoutineContext.Do` to act and observe) and
add it to `Routines.All`. Everything a routine does is recorded as ordinary steps, so its findings replay like any other.

## 5. Model providers

Implement `IModelProvider` (`Supports(provider)`, `CompleteAsync`, `StreamAsync`), register it in `Program.cs`, and add
its prices to `ModelGateway.PricePer1K` so budgets and cost per bug stay accurate. Report `Truncated = true` when the
model stopped at the token limit; the gateway retries once with more room. One adapter must never claim another's
provider name (there is a routing test for this).

## Knowledge sources

Stories and documents come through two ports in `Projects/KnowledgeSources.cs`: `IStorySource` (`StoriesAsync`: key,
title, criteria) and `IDocSource` (`DocumentsAsync`: id, title, text). Adapters exist for files, Jira Cloud (REST v3,
ADF descriptions) and Confluence Cloud (REST v2, storage format). To add a tracker (Azure DevOps, Linear, GitHub
Issues), implement the port, add its kind to `KnowledgeSources.ForAsync` and to project validation, and test it the way
`SimulatorTests` tests the Jira adapter: against a simulator with the real API's shapes. Versioning and diffs are
computed by Bug Hunter (`KnowledgeDiffer`), so a source needs no history of its own.

## Benchmark targets

To benchmark on another app, give it the demo-app harness contract, all behind the harness key and left out of the
app's OpenAPI document: `GET/PUT /api/admin/defects` (with `requires` for defects that live inside a feature),
`GET/PUT /api/test/features`, `GET /api/stories` and `PUT /api/test/stories/{key}` (versioned), `POST /api/test/reset`
(test data) and `POST /api/test/reset-all` (everything). Then add its URL to `DemoShopClient`, one `Exhibits` predicate
and one `DefiningFields` entry per seeded defect in `HuntEngine`, an answer key and scenarios under `ground-truth/`, and
an entry in `APPS` in `scripts/benchmark.mjs`. Demo Hotel (`src/DemoHotel`) is a small, complete example. See
[evaluation.md](evaluation.md).
