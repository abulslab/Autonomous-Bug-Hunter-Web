using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BugHunter.Api.Common;
using BugHunter.Api.Contracts;
using BugHunter.Api.Models;

namespace BugHunter.Api.Hunter;

public record Suspicion(string Title, string Expected, string Actual, string Severity);

// The hunt spent its AI budget: the engine carries on without a model rather than overspend.
public class AiBudgetExceededException(double cap) : Exception($"The hunt reached its AI budget of ${cap:0.00}.");
public record Decision(string Observation, string Reasoning, List<HuntAction> Actions, Suspicion? Suspicion, bool Done, List<PlanItem>? Plan = null);

public class HuntContext
{
    public required Hunt Hunt { get; init; }
    public required PageSnapshot Page { get; set; }
    public required OracleState Oracles { get; init; }
    public int Step { get; set; }
    public string? LastError { get; set; }
    public string? Focus { get; set; } // Guided: the one task to do on this page (the destination's goal)
}

public interface IHuntPolicy { Task<Decision> NextAsync(HuntContext ctx, CancellationToken ct); }

// The model chooses the next actions from a structured page snapshot. It never sees secret values, only {{secret:name}} references.
// Two roles: the driver model decides each step (many short calls); the planner model does the few calls where reasoning
// quality matters (strategy, rules from requirements, probe ideas). Without a planner, the driver does both.
public partial class ModelHuntPolicy(ModelGateway gateway, ResolvedModel model, ResolvedModel? planner = null) : IHuntPolicy
{
    const int MaxBatch = 6;
    ResolvedModel Planner => planner ?? model;
    // Guided hunts write their rules from intents during warm-up; other strategies draft them while exploring.
    public bool DraftsRules { get; init; } = true;
    // Local models are slower and have small context windows: they get a longer deadline and a more compact page view.
    bool Local => model.Provider is "Ollama" or "Local";
    bool PlannerLocal => Planner.Provider is "Ollama" or "Local";
    TimeSpan CallTimeout => Local ? TimeSpan.FromMinutes(3) : TimeSpan.FromSeconds(45);
    TimeSpan PlannerTimeout => PlannerLocal ? TimeSpan.FromMinutes(6) : TimeSpan.FromSeconds(90);

    // Every model call goes through here: the hunt's AI budget, a deadline, usage accounting, and cut-off replies.
    async Task<ChatResponse> CallAsync(Hunt h, ResolvedModel m, ChatRequest request, TimeSpan deadline, CancellationToken ct)
    {
        if (h.MaxCostUsd is double cap && h.Usage.CostUsd >= cap) throw new AiBudgetExceededException(cap);
        using var activity = Telemetry.Source.StartActivity("model.call");
        activity?.SetTag("gen_ai.system", m.Provider); activity?.SetTag("gen_ai.request.model", m.Model); activity?.SetTag("bughunter.purpose", request.AgentName); activity?.SetTag("bughunter.hunt", h.Id);
        using var call = CancellationTokenSource.CreateLinkedTokenSource(ct);
        call.CancelAfter(deadline);
        var res = await gateway.CompleteAsync(m, request, call.Token);
        var cost = ModelGateway.Cost(res.Model, res.TokensIn, res.TokensOut);
        var u = h.Usage; u.ModelCalls++; u.TokensIn += res.TokensIn; u.TokensOut += res.TokensOut; u.CostUsd += cost;
        activity?.SetTag("gen_ai.usage.input_tokens", res.TokensIn); activity?.SetTag("gen_ai.usage.output_tokens", res.TokensOut); activity?.SetTag("bughunter.cost_usd", cost);
        var tags = new TagList { { "provider", m.Provider }, { "model", m.Model }, { "purpose", request.AgentName } };
        Telemetry.ModelCalls.Add(1, tags); Telemetry.ModelCost.Add(cost, tags);
        Telemetry.ModelTokens.Add(res.TokensIn, new TagList { { "direction", "input" }, { "model", m.Model } });
        Telemetry.ModelTokens.Add(res.TokensOut, new TagList { { "direction", "output" }, { "model", m.Model } });
        if (res.Truncated) throw new InvalidOperationException("The model's reply was cut off at its token limit, even after a retry with more room.");
        return res;
    }
    static readonly string[] ActionTypes = ["routine", "click", "fill", "select", "check", "press", "navigate", "back", "reload", "scroll", "done"];

    public static readonly JsonObject Schema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["observation"] = new JsonObject { ["type"] = "string", ["description"] = "What this page is and what changed since the last step." },
            ["reasoning"] = new JsonObject { ["type"] = "string", ["description"] = "Which risk or heuristic the next actions probe, and why." },
            ["actions"] = new JsonObject
            {
                ["type"] = "array", ["maxItems"] = MaxBatch,
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["type"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(ActionTypes.Select(a => (JsonNode)a).ToArray()) },
                        ["ref"] = new JsonObject { ["type"] = "integer" },
                        ["value"] = new JsonObject { ["type"] = "string" },
                        ["url"] = new JsonObject { ["type"] = "string" },
                        ["key"] = new JsonObject { ["type"] = "string" },
                        ["routine"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(Routines.All.Select(r => (JsonNode)r.Name).ToArray()) },
                        ["args"] = new JsonObject { ["type"] = "object", ["description"] = "Routine arguments as strings; name elements by their visible label or name." }
                    },
                    ["required"] = new JsonArray("type")
                }
            },
            ["suspicion"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["title"] = new JsonObject { ["type"] = "string" }, ["expected"] = new JsonObject { ["type"] = "string" },
                    ["actual"] = new JsonObject { ["type"] = "string" }, ["severity"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("High", "Medium", "Low") }
                }
            },
            ["plan"] = new JsonObject
            {
                ["type"] = "array", ["maxItems"] = 12,
                ["description"] = "Your test plan: concrete test ideas, carried to the next turn.",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["idea"] = new JsonObject { ["type"] = "string" },
                        ["status"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("pending", "done", "dropped") }
                    },
                    ["required"] = new JsonArray("idea", "status")
                }
            },
            ["done"] = new JsonObject { ["type"] = "boolean" }
        },
        // Only the actions are required: smaller local models often omit the narrative fields, which then default.
        ["required"] = new JsonArray("actions")
    };

    const string System = """
You are an expert exploratory tester hunting for functional bugs in a web application through a real browser.
Think like a skeptical human tester: learn what the app is for, then deliberately probe the places where it is likely to break.

Each turn you receive the mission, the current page (URL, headings, visible text, and interactive elements as [ref] role "name"), recent history and findings.
Reply with the next 1-6 actions. Fill several fields of one form and then click its submit button in the same turn.
Refs are only valid for the page you were shown; after a click that changes the page, stop the batch and wait for the next snapshot.

Prefer ROUTINES: one routine runs a whole probe reliably (many steps) from a single decision, so your budget goes much further.
Use {"type":"routine","routine":"<name>","args":{...}} and name elements by their visible label or name, not by ref. A routine ends your batch.
{ROUTINES}
Examples of single-routine replies (one routine per turn is fine):
  {"type":"routine","routine":"boundary-values","args":{"field":"Quantity","submit":"Add to cart"}}
  {"type":"routine","routine":"case-variants","args":{"field":"Search products","submit":"Search","term":"Trail"}}
  {"type":"routine","routine":"repeat-submit","args":{"field":"Coupon code","value":"SAVE10","submit":"Apply coupon"}}
  {"type":"routine","routine":"fill-form","args":{"Full name":"Ann Lee","Card number":"4242 4242 4242 4242","Expiry (MM/YY)":"01/20","submit":"Place order"}}
Use single actions only for moves no routine covers.

Actions: click(ref), fill(ref, value), select(ref, value=option label), check(ref, value="true"|"false"), press(key, optional ref), navigate(url on the same site), back, reload, scroll, done.
Test data: use {{secret:NAME}} for any provided secret (for example {{secret:password}}); you never see the real values. Invent other data yourself.

Heuristics to apply (pick what fits the page):
- Boundaries: 0, -1, 1, just above the stated stock or limit, very large numbers, empty, whitespace, 300-character strings.
- Invalid formats: an expiry date before TODAY, card numbers that fail Luhn, malformed email or postcode.
- Repetition and order: apply the same coupon twice, submit twice, go back or reload after submitting.
- Consistency: compare prices and totals between listing, detail, cart, checkout and confirmation pages; check the arithmetic.
- Search: different letter case, partial words, no-result queries.
- Inventory: order more than the stock shown ("Only 3 left").
- State: sign out and in, refresh, revisit; does the cart and order history stay correct?

Work from a test plan, like a tester with a charter:
- Keep "plan" up to date every turn: up to 12 concrete test ideas, each pending, done or dropped. You get it back next turn, so nothing you intend to try is forgotten.
- Add an idea as soon as the page suggests a risk: a stock count ("Only 5 left"), a limit, a coupon code, a price, a validation rule. Mark it done once you have tried it.
- Choose your actions from a pending idea. Once a flow works with ordinary data, do not repeat it with ordinary data: ALREADY TESTED lists what has been covered. Spend most of the budget on risky values and sequences.
- Use TODAY to judge dates: an expiry before TODAY is invalid data, one after it is valid.

The engine also checks the RULES listed in your prompt on every API response; a broken rule is reported for you.
Deterministic oracles automatically check console errors, server errors, cart and tax arithmetic, negative amounts, invalid input that the server accepts, duplicate coupons, overselling and search misses. You do not need to report those.
Report a "suspicion" only for a bug you actually observed on the page that those oracles cannot see (wrong message, wrong page, missing confirmation, data that does not match what you entered). Quote the exact expected and actual. Never report guesses.
Stay on the site. Set done=true when the mission is covered or nothing new can be explored.
""";

    int _drafts, _samplesAtDraft, _stepAtDraft;

    public async Task<Decision> NextAsync(HuntContext ctx, CancellationToken ct)
    {
        // Write rules once the app's API has been seen, and again when new endpoints appear (at most three times).
        var samples = ctx.Oracles.Samples.Count;
        if (DraftsRules && _drafts < 3 && samples >= 2 && (_drafts == 0 || samples >= _samplesAtDraft + 2 && ctx.Step >= _stepAtDraft + 8))
        {
            _drafts++; _samplesAtDraft = samples; _stepAtDraft = ctx.Step;
            try { await DraftRulesAsync(ctx, ct); }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                ctx.Hunt.Notes.Add($"Step {ctx.Step}: the model could not write rules ({(e is OperationCanceledException ? "timed out" : e.Message)}).");
            }
        }
        var contract = new OutputContract(Schema, "bug-hunter", "bug-hunter-v1");
        var user = Prompt(ctx, Local) + "\n\n" + OutputContracts.PromptInstruction(contract);
        var system = System.Replace("{ROUTINES}", string.Join("\n", Routines.All.Select(r => "- " + r.Usage)));
        string text = "";
        List<string> lastErrors = [];
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var res = await CallAsync(ctx.Hunt, model, new ChatRequest(system, user, 0.2, 1500, [], "bug-hunter", null, Schema), CallTimeout, ct);
            var u = ctx.Hunt.Usage;
            text = res.Text;
            var parsed = OutputContracts.Parse(text);
            // Optional fields sent as null (common with local models) are treated as absent.
            if (parsed.Value is JsonObject reply)
            {
                foreach (var k in reply.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList()) reply.Remove(k);
                NormalizeRoutines(reply);
            }
            var errors = parsed.Value is null ? [parsed.Error!] : contract.Validate(parsed.Value).Select(e => e.ToString()).ToList();
            if (errors.Count == 0) return ToDecision((JsonObject)parsed.Value!);
            lastErrors = errors;
            if (attempt == 0) { u.Repairs++; user = Prompt(ctx, Local) + "\n\n" + OutputContracts.RepairInstruction(text, errors); }
        }
        throw new InvalidOperationException($"The model's reply did not satisfy the action contract after one repair ({string.Join("; ", lastErrors.Take(3))}).");
    }

    public static readonly JsonObject RuleSchema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["invariants"] = new JsonObject
            {
                ["type"] = "array", ["maxItems"] = 8,
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["id"] = new JsonObject { ["type"] = "string" }, ["on"] = new JsonObject { ["type"] = "string" },
                        ["check"] = new JsonObject { ["type"] = "string" }, ["claim"] = new JsonObject { ["type"] = "string" },
                        ["requirement"] = new JsonObject { ["type"] = "string" }
                    },
                    ["required"] = new JsonArray("on", "check")
                }
            }
        },
        ["required"] = new JsonArray("invariants")
    };

    const string RuleSystem = """
You write invariants for a web application under test: rules that must ALWAYS hold. The test engine checks every rule against every API response the browser records, so a rule you write is checked many times, with no further model calls.
Write the rules a careful tester would want guarded: arithmetic of totals, limits and quantities, consistency between request and response, validation that must reject bad input.
Write rules from the REQUIREMENTS first (put the story key in "requirement"), then from what the API responses imply.
Copy paths and field names exactly from API RESPONSES SEEN (the examples below are from another app: never reuse their paths or field names). Never restate the values you see (no "total == 32.45"): write the relationship that must hold for any data.
A rule about rejecting bad input checks the request and the status: "status >= 400 || request.amount > 0".
Examples from a different application:
  {"id":"order-total","on":"GET /api/orders/{id}","check":"total == sum(lines, unitPrice * quantity) + fee","claim":"An order's total is its lines plus the fee"}
  {"id":"quantity-positive","on":"POST /api/basket/lines","check":"status >= 400 || request.quantity >= 1","claim":"The basket rejects quantities below 1","requirement":"SHOP-12"}
  {"id":"balance-not-negative","on":"GET /api/account","check":"balance >= 0","claim":"A balance is never negative"}
  {"id":"fee-cap","on":"*","check":"!exists(fee) || fee <= 0.1 * amount","claim":"The fee is at most 10% of the amount"}
{LANGUAGE}
""";

    public static readonly JsonObject ProbeSchema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["probes"] = new JsonObject
            {
                ["type"] = "array", ["maxItems"] = 10,
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["on"] = new JsonObject { ["type"] = "string" }, ["field"] = new JsonObject { ["type"] = "string" },
                        ["values"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["maxItems"] = 6 },
                        ["why"] = new JsonObject { ["type"] = "string" }, ["requirement"] = new JsonObject { ["type"] = "string" }
                    },
                    ["required"] = new JsonArray("on", "field", "values")
                }
            }
        },
        ["required"] = new JsonArray("probes")
    };

    const string ProbeSystem = """
You plan API probes for a tester. The engine resends calls the app really made, changing ONE field to each value you give, and judges every response with deterministic checks and rules.
The engine already tries, for every call: -1, 0, 99999, one more than the stock shown, a past card expiry, a card number failing Luhn, a malformed email, sending twice, and no session. Do not repeat those.
Propose values only someone who read the REQUIREMENTS and the responses would think of: limits stated in a story ("at most 99" -> 100), codes and ids seen in responses (another user's id, an expired or unknown coupon), formats a rule forbids, values just inside and just outside every stated limit.
Use "on" and "field" exactly as listed under CALLS YOU CAN CHANGE. Give each probe a short "why" and the story key it tests, if any.
""";

    // One model call: business-rule probes the engine's fixed catalog would not think of. Executed by the engine, never by the model.
    public async Task<List<Probe>> PlanProbesAsync(HuntContext ctx, CancellationToken ct)
    {
        var h = ctx.Hunt;
        var calls = Probes.Bases(ctx.Oracles).Where(b => b.Event.Method is "POST" or "PUT" or "PATCH" && b.Event.RequestJson() is JsonObject).ToList();
        if (calls.Count == 0) return [];
        var contract = new OutputContract(ProbeSchema, "bug-hunter-probes", "bug-hunter-probes-v1");
        var sb = new StringBuilder();
        sb.AppendLine($"TODAY: {DateTime.UtcNow:yyyy-MM-dd}");
        if (h.Requirements.Count > 0)
        {
            sb.AppendLine("REQUIREMENTS:");
            foreach (var r in h.Requirements) sb.AppendLine($"  {r.Key} {r.Title}\n    {r.Criteria.Replace("\n", "\n    ")}");
        }
        sb.AppendLine("CALLS YOU CAN CHANGE (on: fields):");
        foreach (var (_, e) in calls) sb.AppendLine($"  {Probes.Endpoint(e)}: {string.Join(", ", Probes.Keys((JsonObject)e.RequestJson()!))}");
        sb.AppendLine("API RESPONSES SEEN (one example each):");
        foreach (var s in ctx.Oracles.Samples.Values.Take(PlannerLocal ? 10 : 20)) sb.AppendLine("  " + s);
        sb.AppendLine("\nReply with up to 10 probes.\n" + OutputContracts.PromptInstruction(contract));
        var res = await CallAsync(h, Planner, new ChatRequest(ProbeSystem, sb.ToString(), 0.2, 1200, [], "bug-hunter-probes", null, ProbeSchema), PlannerTimeout, ct);
        var list = new List<Probe>(); var g = 0; var proposed = 0;
        if (OutputContracts.Parse(res.Text).Value is JsonObject reply && reply["probes"] is JsonArray arr)
            foreach (var o in arr.OfType<JsonObject>())
            {
                proposed++;
                var values = (o["values"] as JsonArray ?? []).Select(v => v is null ? "" : J.Text(v)).ToList();
                var req = J.Text(o["requirement"]) is { Length: > 0 } k && h.Requirements.Any(r => r.Key == k.ToUpperInvariant()) ? k.ToUpperInvariant() : null;
                list.AddRange(Probes.FromModel(h, ctx.Oracles, J.Text(o["on"]), J.Text(o["field"]), values, J.Text(o["why"]), req, ref g));
            }
        h.Notes.Add($"The model proposed {proposed} probe{(proposed == 1 ? "" : "s")}; {list.Select(a => a.Group).Distinct().Count()} matched a recorded call and field and were sent.");
        return list;
    }

    static JsonObject Str(string? description = null) => description is null ? new() { ["type"] = "string" } : new() { ["type"] = "string", ["description"] = description };
    static JsonObject StrList(int max = 6) => new() { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["maxItems"] = max };

    // A rule is admitted only if it targets an endpoint the hunt has seen and can be evaluated on at least one recorded
    // response (so it names real fields). Returns why not; null when admitted or an exact duplicate.
    // known: endpoints from the persisted API catalog, so a check on a call this hunt has not made yet is still admitted.
    internal static string? Admit(Hunt h, HuntContext ctx, Invariant rule, IEnumerable<string>? known = null)
    {
        static NetworkEvent Synthetic(string endpoint) { var i = endpoint.IndexOf(' '); return new NetworkEvent { Method = endpoint[..i], Url = "http://catalog" + endpoint[(i + 1)..] }; }
        if (rule.On != "*" && !ctx.Oracles.History.Any(x => InvariantRules.Applies(rule, x.Event)) && !(known ?? []).Any(k => InvariantRules.Applies(rule, Synthetic(k))))
        {
            // Planning runs before every flow has been exercised, so an endpoint not seen yet is often simply not reached
            // yet (the booking a search page leads to). Under the API the app already uses, keep the rule waiting: it is
            // checked from the first matching response, and a rule whose endpoint never appears never yields a finding.
            if (!UnderSeenApi(rule.On, h.Coverage.Endpoints.Concat(known ?? [])))
                return $"on \"{rule.On}\" matches no API call seen so far; use one of: {string.Join(", ", h.Coverage.Endpoints.Take(15))}";
            rule.Deferred = true;
        }
        var seen = ctx.Oracles.History.Where(x => InvariantRules.Applies(rule, x.Event)).Select(x => x.Event).TakeLast(20).ToList();
        if (seen.Count > 0 && !rule.Check.Contains("prev") && seen.All(e => InvariantRules.Evaluate(rule, e, null).Holds is null))
        {
            var example = ctx.Oracles.Samples.Values.FirstOrDefault(v => seen.Any(e => v.StartsWith($"{e.Method} {e.Path} ")));
            return $"check \"{rule.Check}\" uses fields that no matching response has" + (example is null ? "" : $"; the response looks like: {example}");
        }
        if (h.Invariants.Count >= 40 || h.Invariants.Any(x => Same(x.Check, rule.Check))) return null;
        if (rule.Requirement is { } key && h.Requirements.All(r => r.Key != key)) rule.Requirement = null; // only cite stories the hunt has
        rule.Id = InvariantRules.Unique(h.Invariants, rule.Id);
        h.Invariants.Add(rule);
        return null;
    }

    // "POST /api/bookings" is under the API of "GET /api/rooms"; "POST /v2/orders" is not.
    static bool UnderSeenApi(string on, IEnumerable<string> endpoints)
    {
        static string? Base(string endpoint)
        {
            var path = endpoint.Contains(' ') ? endpoint[(endpoint.IndexOf(' ') + 1)..] : endpoint;
            var parts = path.Trim().TrimStart('/').Split('/');
            return parts.Length > 1 ? parts[0].ToLowerInvariant() : null;
        }
        return Base(on) is { } b && endpoints.Any(e => Base(e) == b);
    }

    public static readonly JsonObject IntentSchema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["model"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["domain"] = Str("What the app is for, in one sentence."), ["entities"] = StrList(), ["workflows"] = StrList(), ["risks"] = StrList() }
            },
            ["intents"] = new JsonObject
            {
                ["type"] = "array", ["maxItems"] = 6,
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["title"] = Str("What the user wants to achieve, e.g. \"Buy a product with a coupon\"."),
                        ["actor"] = Str("Who: shopper, guest, admin."),
                        ["state"] = Str("The number of the state on THE MAP where the user does it, e.g. \"S3\"."),
                        ["priority"] = new JsonObject { ["type"] = "integer", ["description"] = "1 = test first (business value and risk) ... 5 = last." },
                        ["requirement"] = Str("The story key it comes from, if any."),
                        ["preconditions"] = StrList(4),
                        ["success"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["maxItems"] = 4, ["description"] = "Observable facts that prove the intent succeeded." },
                        ["checks"] = new JsonObject
                        {
                            ["type"] = "array", ["maxItems"] = 3, ["description"] = "The success facts as rules on API responses, in the rule language.",
                            ["items"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["on"] = Str(), ["check"] = Str(), ["claim"] = Str() }, ["required"] = new JsonArray("on", "check") }
                        },
                        ["variants"] = new JsonObject
                        {
                            ["type"] = "array", ["maxItems"] = 4,
                            ["items"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["name"] = Str("The risky variation, e.g. \"apply the same coupon twice\"."),
                                    ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("boundary", "invalid", "repeat", "order", "session", "consistency") },
                                    ["routine"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(Routines.All.Select(r => (JsonNode)r.Name).ToArray()) },
                                    ["args"] = new JsonObject { ["type"] = "object" }
                                },
                                ["required"] = new JsonArray("name")
                            }
                        }
                    },
                    ["required"] = new JsonArray("title", "state", "success")
                }
            }
        },
        ["required"] = new JsonArray("model", "intents")
    };

    const string IntentSystem = """
You are the test architect for a web application. The engine has surveyed it: THE MAP lists its states (page types, signed in or not) with what each offers, and API lists the calls it makes with their fields. A navigator can drive to any state on the map; built-in checks judge every response; replays confirm every finding without you.
Do the part only understanding can do:
1. MODEL the application: its domain, the things it manages (entities), the journeys users take (workflows), and where it is most likely to be wrong (risks).
2. Write the INTENTS a tester must cover: what users come to do, most important first. For each:
   - "state": the number of the state on THE MAP where the user does it ("S3").
   - "success": observable facts that prove it worked, taken from the REQUIREMENTS and SKILLS where they apply.
   - "checks": those facts as rules in the rule language below, on the API calls listed under API (copy endpoint paths and field names exactly).
   - "variants": the risky variations a careful tester would try (boundary values, invalid input, repeats, wrong order, no session, consistency between pages). Prefer a routine with arguments that name fields and buttons exactly as the state's "has" list shows them.
Cover the REQUIREMENTS first. Do not plan intents for what FINDINGS SO FAR already proved broken.
{LANGUAGE}
""";

    // The warm-up's one planner call: the app model and its intents, each tied to a reachable state, with success checks
    // admitted as rules only after they parse and evaluate on recorded traffic.
    public async Task<List<Intent>> PlanIntentsAsync(HuntContext ctx, AppMap map, IReadOnlyList<SkillPack> packs, CancellationToken ct)
    {
        var h = ctx.Hunt;
        var here = AppMap.StateOf(ctx.Page);
        var homeState = map.States.FirstOrDefault(s => s.Url.TrimEnd('/') == h.Url.TrimEnd('/'))?.Id;
        var reachable = map.Reachable(here);
        if (homeState is not null) reachable.UnionWith(map.Reachable(homeState));
        var choices = map.States.Where(s => reachable.Contains(s.Id)).OrderByDescending(s => s.Visits).Take(PlannerLocal ? 12 : 25).ToList();
        var ids = choices.Select(s => s.Id).ToList();
        var contract = new OutputContract(IntentSchema, "bug-hunter-intents", "bug-hunter-intents-v1");

        var sb = new StringBuilder();
        sb.AppendLine($"MISSION: {(string.IsNullOrWhiteSpace(h.Mission) ? "Find functional bugs." : h.Mission)}");
        sb.AppendLine($"TODAY: {DateTime.UtcNow:yyyy-MM-dd}");
        if (h.Requirements.Count > 0)
        {
            sb.AppendLine("REQUIREMENTS:");
            foreach (var r in h.Requirements) sb.AppendLine($"  {r.Key} {r.Title}: {Regex.Replace(r.Criteria, @"\s*\n\s*", " / ")}");
        }
        if (!string.IsNullOrWhiteSpace(h.Grounding)) sb.AppendLine($"PRODUCT NOTES:\n{(h.Grounding.Length > 3000 ? h.Grounding[..3000] : h.Grounding)}");
        foreach (var p in packs.Take(3)) sb.AppendLine($"SKILLS: {p.Title}\n{(p.Body.Length > 1800 ? p.Body[..1800] : p.Body)}");
        sb.AppendLine($"THE MAP (you are at \"{here}\"). In \"state\", answer with the state's number, for example \"S3\":");
        sb.Append(map.Numbered(choices, here));
        sb.AppendLine("API (method path [statuses seen] request and response fields):");
        sb.AppendLine(ApiCatalog.Describe(map, PlannerLocal ? 12 : 30));
        sb.AppendLine($"FINDINGS SO FAR: {(h.Findings.Count == 0 ? "none" : string.Join("; ", h.Findings.Select(f => f.Title).Distinct()))}");
        sb.AppendLine("ROUTINES:");
        foreach (var r in Routines.All) sb.AppendLine("  - " + r.Usage);
        sb.AppendLine($"\nPlan at most {(PlannerLocal ? 4 : 6)} intents, each with at most 4 variants and 3 checks.\n" + OutputContracts.PromptInstruction(contract));

        var system = IntentSystem.Replace("{LANGUAGE}", InvariantRules.Language);
        var res = await CallAsync(h, Planner, new ChatRequest(system, sb.ToString(), 0.3, PlannerLocal ? 1500 : 4000, [], "bug-hunter-intents", null, IntentSchema), PlannerTimeout, ct);
        if (OutputContracts.Parse(res.Text).Value is not JsonObject reply) throw new InvalidOperationException("The planner's reply was not JSON.");

        static List<string> L(JsonNode? n, int max = 8) => (n as JsonArray ?? []).Select(x => x is null ? "" : J.Text(x)).Where(x => x.Length > 0).Take(max).ToList();
        if (reply["model"] is JsonObject m)
            h.AppModel = map.Model = new AppModel { Domain = J.Text(m["domain"]), Entities = L(m["entities"]), Workflows = L(m["workflows"]), Risks = L(m["risks"]) };

        var intents = new List<Intent>(); var dropped = 0; var checkErrors = new List<string>(); var checksAdded = 0;
        foreach (var o in (reply["intents"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var state = AppMap.Match(J.Text(o["state"]), ids);
            if (state is null || J.Text(o["title"]).Length == 0) { dropped++; continue; }
            var intent = new Intent
            {
                Id = $"I{intents.Count + 1}", Goal = J.Text(o["title"]), State = state, Actor = J.Text(o["actor"]),
                Priority = o["priority"] is JsonValue pv && pv.TryGetValue<int>(out var pr) ? Math.Clamp(pr, 1, 5) : 3,
                Requirement = J.Text(o["requirement"]) is { Length: > 0 } k && h.Requirements.Any(r => r.Key == k.ToUpperInvariant()) ? k.ToUpperInvariant() : null,
                Preconditions = L(o["preconditions"], 4), Success = L(o["success"], 4),
                Variants = (o["variants"] as JsonArray ?? []).OfType<JsonObject>().Take(4).Select(v =>
                {
                    var routine = Routines.Get(J.Text(v["routine"]))?.Name;
                    return new IntentVariant
                    {
                        Name = J.Text(v["name"]), Kind = J.Text(v["kind"]), Routine = routine,
                        Args = routine is not null && v["args"] is JsonObject a ? a.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => J.Text(kv.Value)) : null
                    };
                }).Where(v => v.Name.Length > 0).ToList()
            };
            // Success facts become rules the engine checks on every response, tied to this intent.
            foreach (var c in (o["checks"] as JsonArray ?? []).OfType<JsonObject>().Take(3))
            {
                var (rule, err) = InvariantRules.Create(null, J.Text(c["on"]), J.Text(c["check"]), J.Text(c["claim"]) is { Length: > 0 } cl ? cl : intent.Goal, intent.Requirement, "Model", ctx.Step);
                if (rule is null) { checkErrors.Add(err!); continue; }
                rule.Intent = intent.Id;
                var before = h.Invariants.Count;
                if (Admit(h, ctx, rule, map.Apis.Select(a => a.Endpoint)) is { } why) { checkErrors.Add(why); continue; }
                if (h.Invariants.Count > before) { intent.RuleIds.Add(rule.Id); checksAdded++; }
            }
            intents.Add(intent);
        }
        h.Notes.Add($"Planner: {intents.Count} intent{(intents.Count == 1 ? "" : "s")} with {intents.Sum(i => i.Variants.Count)} variants and {checksAdded} success checks"
            + (dropped > 0 ? $"; {dropped} intent{(dropped == 1 ? "" : "s")} dropped (state not on the map)" : "")
            + (intents.SelectMany(i => i.RuleIds).Count(id => h.Invariants.Any(r => r.Id == id && r.Deferred)) is var waiting and > 0 ? $"; {waiting} wait for an endpoint not reached yet" : "")
            + (checkErrors.Count > 0 ? $"; {checkErrors.Count} check{(checkErrors.Count == 1 ? "" : "s")} rejected ({checkErrors[0]})" : "") + ".");
        return intents;
    }

    // "response.tax == round(...)" and "tax == round(...)" are the same rule.
    static bool Same(string a, string b) => Norm(a) == Norm(b);
    static string Norm(string c) => Regex.Replace(c.Replace("response.", ""), @"\s+", "");

    async Task DraftRulesAsync(HuntContext ctx, CancellationToken ct)
    {
        var h = ctx.Hunt;
        var contract = new OutputContract(RuleSchema, "bug-hunter-rules", "bug-hunter-rules-v1");
        var sb = new StringBuilder();
        sb.AppendLine($"MISSION: {(string.IsNullOrWhiteSpace(h.Mission) ? "Find functional bugs." : h.Mission)}");
        sb.AppendLine($"TODAY: {DateTime.UtcNow:yyyy-MM-dd}");
        if (h.Requirements.Count > 0)
        {
            sb.AppendLine("REQUIREMENTS:");
            foreach (var r in h.Requirements) sb.AppendLine($"  {r.Key} {r.Title}\n    {r.Criteria.Replace("\n", "\n    ")}");
        }
        sb.AppendLine("API RESPONSES SEEN (one example each):");
        foreach (var s in ctx.Oracles.Samples.Values.Take(PlannerLocal ? 10 : 20)) sb.AppendLine("  " + s);
        if (h.Invariants.Count > 0)
        {
            sb.AppendLine("RULES ALREADY CHECKED (do not repeat them):");
            foreach (var r in h.Invariants) sb.AppendLine($"  {r.On}: {r.Check}");
        }
        sb.AppendLine();
        sb.AppendLine("Reply with up to 8 new invariants.");
        var system = RuleSystem.Replace("{LANGUAGE}", InvariantRules.Language);
        var user = sb + "\n" + OutputContracts.PromptInstruction(contract);
        var total = 0;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var res = await CallAsync(h, Planner, new ChatRequest(system, user, 0.2, 1500, [], "bug-hunter-rules", null, RuleSchema), PlannerTimeout, ct);
            var u = h.Usage;
            var parsed = OutputContracts.Parse(res.Text);
            var errors = new List<string>(); var added = 0;
            if (parsed.Value is JsonObject reply && reply["invariants"] is JsonArray list)
                foreach (var o in list.OfType<JsonObject>())
                {
                    var (rule, err) = InvariantRules.Create(J.Text(o["id"]), J.Text(o["on"]), J.Text(o["check"]), J.Text(o["claim"]), J.Text(o["requirement"]), "Model", ctx.Step);
                    if (rule is null) { errors.Add(err!); continue; }
                    var before = h.Invariants.Count;
                    if (Admit(h, ctx, rule) is { } why) errors.Add(why);
                    else added += h.Invariants.Count - before;
                }
            else errors.Add(parsed.Error ?? "The reply has no \"invariants\" array.");
            total += added;
            if (errors.Count == 0 || attempt == 1)
            {
                h.Notes.Add($"Step {ctx.Step}: the model wrote {total} rule{(total == 1 ? "" : "s")}{(errors.Count > 0 ? $"; {errors.Count} were unusable and dropped ({errors[0]})" : "")}.");
                return;
            }
            // Keep the good rules and ask once for corrected versions of the rest.
            u.Repairs++;
            user = sb + "\nSome of your rules were rejected. Reply with corrected versions of ONLY these, as {\"invariants\":[...]}:\n- " + string.Join("\n- ", errors)
                + "\n\n" + OutputContracts.PromptInstruction(contract);
        }
    }

    // Smaller models write {"type":"fill-form", ...}: treat a routine name used as the type as a routine call,
    // and gather its other fields into args.
    static void NormalizeRoutines(JsonObject reply)
    {
        string[] own = ["type", "ref", "value", "url", "key", "routine", "args"];
        foreach (var a in (reply["actions"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (Routines.Get(J.Text(a["type"])) is not { } r) continue;
            a["routine"] = r.Name; a["type"] = "routine";
            if (a["args"] is not JsonObject args) a["args"] = args = new JsonObject();
            foreach (var k in a.Select(kv => kv.Key).Where(k => !own.Contains(k)).ToList()) { var v = a[k]; a.Remove(k); args[k] = v?.DeepClone(); }
        }
    }

    static Decision ToDecision(JsonObject o)
    {
        var actions = (o["actions"] as JsonArray ?? []).OfType<JsonObject>().Take(MaxBatch).Select(a => new HuntAction
        {
            Type = J.Text(a["type"]),
            Ref = a["ref"] is JsonValue r && r.TryGetValue<int>(out var i) ? i : null,
            Value = a["value"] is null ? null : J.Text(a["value"]),
            Url = a["url"] is null ? null : J.Text(a["url"]),
            Key = a["key"] is null ? null : J.Text(a["key"]),
            Routine = a["routine"] is null ? null : J.Text(a["routine"]),
            Args = a["args"] is JsonObject args ? args.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => J.Text(kv.Value)) : null
        }).ToList();
        // Smaller models set done=true while still proposing actions; only a reply with no actions ends the hunt.
        var done = (o["done"] is JsonValue d && d.TryGetValue<bool>(out var b) && b || actions.Any(a => a.Type == "done")) && actions.All(a => a.Type == "done");
        Suspicion? s = o["suspicion"] is JsonObject so && J.Text(so["title"]) is { Length: > 0 } t && J.Text(so["actual"]) is { Length: > 0 } actual
            ? new(t, J.Text(so["expected"]), actual, J.Text(so["severity"]) is "High" or "Low" ? J.Text(so["severity"]) : "Medium") : null;
        var plan = (o["plan"] as JsonArray ?? []).OfType<JsonObject>().Take(12)
            .Select(p => new PlanItem(J.Text(p["idea"]), J.Text(p["status"]) is "done" or "dropped" ? J.Text(p["status"]) : "pending"))
            .Where(p => p.Idea.Length > 0).DistinctBy(p => p.Idea.Trim().ToLowerInvariant()).ToList();
        return new(J.Text(o["observation"]), J.Text(o["reasoning"]), actions.Where(a => a.Type != "done").ToList(), s, done, plan);
    }

    static string Prompt(HuntContext ctx, bool compact)
    {
        var h = ctx.Hunt; var p = ctx.Page;
        var sb = new StringBuilder();
        sb.AppendLine($"MISSION: {(string.IsNullOrWhiteSpace(h.Mission) ? "Explore the application and find functional bugs." : h.Mission)}");
        if (ctx.Focus is { } focus)
            sb.AppendLine($"YOUR TASK NOW, ON THIS PAGE: {focus}. The navigator brought you here. Do only this, in at most two turns, then set done=true. Do not sign out or leave the page unless the task needs it.");
        sb.AppendLine($"TODAY: {DateTime.UtcNow:yyyy-MM-dd}");
        if (h.SweepSteps is int swept)
            sb.AppendLine($"PHASE: a deterministic sweep already ran steps 1-{swept}: it filled every form with boundary and invalid values and visited each page type. " +
                "Its results are in ALREADY TESTED and FINDINGS. Do not redo it. Spend your steps on what needs judgment: limits stated on pages, multi-step sequences, consistency between pages, repeated actions.");
        sb.AppendLine($"STEP {ctx.Step} of {h.MaxSteps}. Irreversible actions (place order, pay, delete): {(h.AllowIrreversible ? "allowed on this test environment" : "blocked; do not attempt them")}.");
        sb.AppendLine($"SECRETS AVAILABLE: {(h.SecretNames.Count == 0 ? "none" : string.Join(", ", h.SecretNames.Select(n => "{{secret:" + n + "}}")))}");
        sb.AppendLine($"PAGES VISITED: {string.Join(", ", h.Coverage.Pages.TakeLast(25))}");
        if (h.Findings.Count > 0) sb.AppendLine($"FINDINGS SO FAR: {string.Join("; ", h.Findings.Select(f => f.Title))}");
        if (h.Requirements.Count > 0)
        {
            sb.AppendLine("REQUIREMENTS UNDER TEST:");
            foreach (var r in h.Requirements) sb.AppendLine($"  {r.Key} {r.Title}: {Regex.Replace(r.Criteria, @"\s*\n\s*", " / ")}");
        }
        if (h.Invariants.Count > 0)
        {
            // Rules are checked automatically; the model's job is to produce the traffic that exercises them.
            sb.AppendLine("RULES THE ENGINE CHECKS ON EVERY RESPONSE (exercise the untested ones with risky data):");
            foreach (var r in h.Invariants.Take(compact ? 10 : 20))
                sb.AppendLine($"  [{r.Status}{(r.Checks > 0 ? $" {r.Holds}/{r.Checks}" : "")}] {r.Claim} ({r.On})");
        }
        sb.AppendLine("YOUR TEST PLAN:");
        if (h.Plan.Count == 0) sb.AppendLine("  (empty: start one from what you see)");
        foreach (var item in h.Plan) sb.AppendLine($"  [{item.Status}] {item.Idea}");
        sb.AppendLine("ALREADY TESTED:");
        foreach (var line in Tested(h)) sb.AppendLine("  " + line);
        sb.AppendLine("RECENT STEPS:");
        foreach (var s in h.Steps.TakeLast(compact ? 8 : 12))
            sb.AppendLine($"  {s.N}. {Describe(s.Action, s.Target)}{(s.Ok ? "" : " -> ERROR " + s.Error)}{(s.Blocked is null ? "" : " -> BLOCKED " + s.Blocked)}{(s.Requests.Count > 0 ? " -> " + string.Join(", ", s.Requests.Take(3)) : "")}");
        if (ctx.LastError is not null) sb.AppendLine($"LAST ACTION FAILED: {ctx.LastError}");
        sb.AppendLine();
        sb.AppendLine($"CURRENT PAGE: {p.Url}");
        sb.AppendLine($"TITLE: {p.Title}");
        if (p.Headings.Count > 0) sb.AppendLine($"HEADINGS: {string.Join(" | ", p.Headings)}");
        if (p.Dialogs.Count > 0) sb.AppendLine($"DIALOGS: {string.Join(" | ", p.Dialogs.Select(d => d.Message))}");
        sb.AppendLine("ELEMENTS:");
        foreach (var e in p.Elements.Take(compact ? 70 : 120)) sb.AppendLine("  " + Element(e));
        sb.AppendLine("VISIBLE TEXT:");
        var max = compact ? 1500 : 3000;
        sb.AppendLine(p.Text.Length > max ? p.Text[..max] + "…" : p.Text);
        return sb.ToString();
    }

    // Facts from the recorded steps, not the model's memory: submissions per endpoint and the values tried in each field.
    public static List<string> Tested(Hunt h)
    {
        var lines = new List<string>();
        var submits = h.Steps.SelectMany(s => s.Requests)
            .Select(r => Regex.Match(r, @"^(POST|PUT|PATCH|DELETE) (\S+) → (\S+)$")).Where(m => m.Success)
            .GroupBy(m => $"{m.Groups[1].Value} {IdSegments().Replace(m.Groups[2].Value, "/{id}")}");
        foreach (var g in submits)
            lines.Add($"{g.Key}: {string.Join(", ", g.GroupBy(m => m.Groups[3].Value).Select(s => $"{s.Key} ×{s.Count()}"))}");
        var fills = h.Steps.Where(s => s.Ok && s.Action.Type == "fill" && s.Target is not null)
            .GroupBy(s => s.Target!.Label is { Length: > 0 } l ? l : s.Target!.Name);
        foreach (var g in fills)
        {
            var values = g.Select(s => s.Action.Value ?? "").Distinct().Select(v => v.Length > 30 ? v[..30] + "…" : v).ToList();
            lines.Add($"\"{g.Key}\" values tried: {string.Join(" | ", values.TakeLast(8).Select(v => v.Length == 0 ? "(empty)" : v))}");
        }
        if (lines.Count == 0) lines.Add("(nothing yet)");
        return lines;
    }

    public static string Element(ElementInfo e)
    {
        var sb = new StringBuilder($"[{e.Ref}] {e.Role} \"{e.Name}\"");
        if (e.Type is { } t && t is not ("text" or "submit" or "button")) sb.Append($" type={t}");
        if (e.Value is { Length: > 0 } v) sb.Append($" value=\"{v}\"");
        if (e.Href is { } href) sb.Append($" -> {href}");
        if (e.Options is { Count: > 0 } o) sb.Append($" options=[{string.Join(", ", o)}]");
        if (e.Checked is true) sb.Append(" checked");
        if (e.Disabled is true) sb.Append(" disabled");
        if (e.Required is true) sb.Append(" required");
        if (e.Invalid is { Length: > 0 } why) sb.Append($" invalid=\"{why}\"");
        if (e.Form is { } f) sb.Append($" form#{f}");
        return sb.ToString();
    }

    public static string Describe(HuntAction a, ElementInfo? t) => a.Type switch
    {
        "fill" => $"fill {Target(t, a)} with \"{(a.Value is { Length: > 60 } v ? v[..60] + $"… ({v.Length} chars)" : a.Value)}\"",
        "select" => $"select \"{a.Value}\" in {Target(t, a)}",
        "check" => $"{(a.Value == "false" ? "uncheck" : "check")} {Target(t, a)}",
        "press" => $"press {a.Key ?? "Enter"}{(t is null ? "" : " in " + Target(t, a))}",
        "navigate" => $"navigate to {a.Url}",
        "routine" => $"routine {a.Routine} {string.Join(", ", (a.Args ?? []).Select(kv => $"{kv.Key}=\"{kv.Value}\""))}",
        "request" => $"API {a.Key} {a.Args?.GetValueOrDefault("path")}: {a.Args?.GetValueOrDefault("label")}",
        "click" => $"click {Target(t, a)}",
        _ => a.Type
    };

    static string Target(ElementInfo? t, HuntAction a) => t is null ? $"[{a.Ref}]" : $"{t.Role} \"{t.Name}\"";
}

public partial class ModelHuntPolicy
{
    [GeneratedRegex(@"/(?:[A-Za-z]{1,4}-)?\d+(?=/|$)")] private static partial Regex IdSegments();
}

// Hybrid: a deterministic sweep first (forms, widgets, page types with boundary and invalid values), then the model
// for the rest of the budget, told what the sweep covered so it spends its steps on what needs judgment.
public class HybridHuntPolicy(HeuristicHuntPolicy sweep, ModelHuntPolicy model, int sweepBudget) : IHuntPolicy
{
    bool _handedOver;
    public string LastDriver { get; private set; } = "Heuristic";
    public ModelHuntPolicy Model => model;
    public bool HandedOver => _handedOver;

    public async Task<Decision> NextAsync(HuntContext ctx, CancellationToken ct)
    {
        if (!_handedOver && ctx.Step <= sweepBudget)
        {
            var d = await sweep.NextAsync(ctx, ct);
            if (!d.Done) { LastDriver = "Heuristic"; return d; }
        }
        if (!_handedOver)
        {
            _handedOver = true;
            ctx.Hunt.SweepSteps = ctx.Step - 1;
            ctx.Hunt.Notes.Add($"The heuristic sweep covered steps 1-{ctx.Step - 1}; the model took over from step {ctx.Step}.");
        }
        LastDriver = "Model";
        return await model.NextAsync(ctx, ct);
    }
}

// Offline explorer used without a live model: deterministic tours over forms, widgets and links with boundary and invalid inputs.
// It is the no-AI baseline the model policy is measured against.
public partial class HeuristicHuntPolicy : IHuntPolicy
{
    readonly Dictionary<string, int> _tried = new();
    // Per page template: how often it was visited and the app state (count of submitted requests) at the last visit.
    readonly Dictionary<string, (int Count, int State)> _visits = new();
    readonly Dictionary<string, string> _frontier = new(); // template -> absolute URL of a link seen anywhere
    string? _lastPage;
    int _idle;

    // Pages that differ only by an id (product/p-101, orders/42) are one template: exploring one instance covers the rest.
    static string PageKey(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? Template(u.AbsolutePath + u.Fragment.Split('?')[0]) : Template(url.Split('?')[0]);
    static string Template(string path) => IdSegment().Replace(path, "/{id}");
    static bool Fillable(ElementInfo e) => e.Role is "textbox" or "searchbox" or "spinbutton" && e.Disabled != true;
    static bool Clickable(ElementInfo e) => e.Role is "button" or "link" && e.Disabled != true;

    public Task<Decision> NextAsync(HuntContext ctx, CancellationToken ct)
    {
        var p = ctx.Page; var page = PageKey(p.Url);
        var state = ctx.Hunt.Coverage.FormsSubmitted;
        if (page != _lastPage) { _visits[page] = (_visits.GetValueOrDefault(page).Count + 1, state); _lastPage = page; }
        var els = p.Elements;
        foreach (var l in els.Where(e => e.Role == "link" && e.Href is not null))
            if (Uri.TryCreate(new Uri(p.Url), l.Href, out var abs) && abs.Host == new Uri(p.Url).Host) _frontier.TryAdd(LinkTarget(l), abs.ToString());

        // 1. Forms: fill every field with the next input variant, then submit.
        foreach (var g in els.Where(e => e.Form is not null).GroupBy(e => e.Form!.Value))
        {
            var fields = g.Where(Fillable).ToList();
            var submit = g.Where(e => e.Role == "button" && e.Disabled != true).LastOrDefault();
            if (fields.Count == 0 || submit is null) continue;
            // A site-wide search form is the same form on every page; try it once overall, not once per page.
            var scope = fields.Any(f => f.Role == "searchbox" || Regex.IsMatch($"{f.Name} {f.Placeholder}", "search", RegexOptions.IgnoreCase)) ? "site" : page;
            var sig = $"{scope}|form|{string.Join(",", fields.Select(f => f.Name))}";
            // One invalid value at a time, every other field plausible, so a rejection is caused by that value alone and
            // one bad value never hides another (an expired card with a valid number); then one fully plausible submission,
            // which moves the app on to whatever lies behind the form. Every variant list ends with the plausible value.
            var variants = fields.Select(f => Variants(f, ctx, p)).ToList();
            var invalid = Enumerable.Range(0, variants.Max(v => v.Count - 1)).SelectMany(i => variants.Select((v, f) => (Field: f, Index: i)).Where(x => x.Index < variants[x.Field].Count - 1)).Take(4).ToList();
            var round = _tried.GetValueOrDefault(sig);
            if (round > invalid.Count) continue;
            _tried[sig] = round + 1;
            var acts = fields.Select((f, i) => new HuntAction { Type = "fill", Ref = f.Ref, Value = round < invalid.Count && invalid[round].Field == i ? variants[i][invalid[round].Index] : variants[i][^1] }).ToList();
            acts.Add(new HuntAction { Type = "click", Ref = submit.Ref });
            return Ok($"Form '{submit.Name}' on {page}", round < invalid.Count
                ? $"Round {round + 1} of {invalid.Count + 1}: {fields[invalid[round].Field].Name} gets a boundary or invalid value, the rest plausible ones."
                : $"Round {round + 1} of {invalid.Count + 1}: every field plausible.", acts);
        }

        // 2. Widgets: a field outside any form paired with the button that follows it (quantity + Update, coupon + Apply).
        for (var i = 0; i < els.Count; i++)
        {
            var f = els[i];
            if (f.Form is not null || !Fillable(f)) continue;
            var btn = els.Skip(i + 1).Take(3).FirstOrDefault(e => e.Role == "button" && e.Form is null && e.Disabled != true);
            if (btn is null) continue;
            var sig = $"{page}|widget|{f.Name}|{btn.Name}";
            var variants = Variants(f, ctx, p);
            var round = _tried.GetValueOrDefault(sig);
            if (round >= variants.Count) continue;
            _tried[sig] = round + 1;
            return Ok($"Widget '{f.Name}' + '{btn.Name}' on {page}", $"Value variant {round + 1} of {variants.Count}.",
                [new() { Type = "fill", Ref = f.Ref, Value = variants[round] }, new() { Type = "click", Ref = btn.Ref }]);
        }

        // 3. Links to pages not seen since the app state last changed, progress links (cart, checkout) first; then other buttons.
        foreach (var l in els.Where(e => e.Role == "link" && e.Href is not null).OrderBy(e => Progress().IsMatch(e.Name) ? 0 : 1))
        {
            var target = LinkTarget(l);
            if (target == page || !Worth(target, state)) continue;
            return Ok($"Link '{l.Name}'", !_visits.ContainsKey(target) ? $"Visit {target}, not explored yet." : $"Revisit {target}: the app state changed since the last visit.", [new() { Type = "click", Ref = l.Ref }]);
        }
        foreach (var b in els.Where(e => e.Role == "button" && e.Form is null && Clickable(e)))
        {
            var sig = $"{page}|button|{b.Name}";
            var limit = Regex.IsMatch(b.Name, "sign out|log out|logout", RegexOptions.IgnoreCase) ? 0 : 1;
            if (_tried.GetValueOrDefault(sig) >= limit) continue;
            _tried[sig] = _tried.GetValueOrDefault(sig) + 1;
            return Ok($"Button '{b.Name}' on {page}", "Exercise each action on the page.", [new() { Type = "click", Ref = b.Ref }]);
        }
        // 4. Nothing new here: jump to a page seen elsewhere that is still worth a visit.
        if (_frontier.Where(f => f.Key != page && Worth(f.Key, state)).Select(f => f.Value).FirstOrDefault() is { } next)
            return Ok($"Nothing new on {page}.", $"Navigate to {next}, linked from an earlier page and not explored in the current state.", [new() { Type = "navigate", Url = next }]);
        if (++_idle > 2) return Task.FromResult(new Decision("Nothing new to explore.", "Every form, widget and link reachable from here has been tried.", [], null, true));
        return Task.FromResult(new Decision("Dead end.", "Go back and look for unexplored paths.", [new() { Type = "back" }], null, false));
    }

    bool Worth(string target, int state) { var seen = _visits.GetValueOrDefault(target); return seen.Count == 0 || (seen.Count < 4 && seen.State != state); }
    static string LinkTarget(ElementInfo l) => l.Href!.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? PageKey(l.Href) : Template(l.Href!.Split('?')[0]);

    Task<Decision> Ok(string obs, string why, List<HuntAction> acts) { _idle = 0; return Task.FromResult(new Decision(obs, why, acts, null, false)); }

    static string Pick(List<string> v, int round) => v[Math.Min(round, v.Count - 1)];

    // Value variants by field meaning, most likely to expose a bug first.
    static List<string> Variants(ElementInfo f, HuntContext ctx, PageSnapshot p)
    {
        var name = $"{f.Label} {f.Name} {f.Placeholder} {f.Type}".ToLowerInvariant();
        string Secret(string key, string fallback) => ctx.Hunt.SecretNames.FirstOrDefault(n => n.Contains(key, StringComparison.OrdinalIgnoreCase)) is { } s ? "{{secret:" + s + "}}" : fallback;
        // Invalid and boundary values first; the plausible value is always last (the form rounds rely on it).
        if (name.Contains("email")) return ["not-an-email", Secret("email", "tester@example.com")];
        if (name.Contains("password")) return [Secret("password", "Password123!")];
        if (Regex.IsMatch(name, "search|query")) return SearchTerms(ctx);
        if ((f.Type == "date" || DateField().IsMatch(name)) && !Regex.IsMatch(name, "expir|mm/yy"))
        {
            // Past and reversed ranges first (the classic date bugs), then a valid range a week ahead.
            var end = Regex.IsMatch(name, @"\b(out|end|to|until|departure|return)\b|check-?out", RegexOptions.IgnoreCase);
            var fmt = name.Contains("dd/mm") ? "dd/MM/yyyy" : name.Contains("mm/dd") ? "MM/dd/yyyy" : "yyyy-MM-dd";
            string Day(int d) => DateTime.UtcNow.Date.AddDays(d).ToString(fmt, System.Globalization.CultureInfo.InvariantCulture);
            return end ? [Day(-3), Day(10)] : [Day(-1), Day(7)];
        }
        if (Regex.IsMatch(name, "coupon|promo|voucher|discount"))
        {
            var code = CouponHint().Match(p.Text) is { Success: true } m ? m.Value : "SAVE10";
            return ["NOTACODE", code, code]; // the same valid code twice: a coupon must apply once
        }
        if (Regex.IsMatch(name, "card number|card no|cardnumber")) return ["1234 5678 9012 3456", Secret("card", "4242 4242 4242 4242")];
        if (Regex.IsMatch(name, "expir|mm/yy")) return ["01/20", Secret("expiry", "12/30")];
        if (Regex.IsMatch(name, "cvv|cvc|security code")) return [Secret("cvv", "123")];
        if (Regex.IsMatch(name, "zip|postal|postcode")) return ["ABCDE", "12345"];
        if (Regex.IsMatch(name, "quantity|qty") || f.Role == "spinbutton")
        {
            var v = new List<string> { "-1", "0" };
            if (StockHint().Match(p.Text) is { Success: true } m && int.TryParse(m.Groups[m.Groups[1].Success ? 1 : 2].Value, out var n)) v.Add((n + 1).ToString());
            v.Add("1000"); v.Add("2");
            return v;
        }
        if (name.Contains("city")) return ["Springfield"];
        if (Regex.IsMatch(name, "address|street")) return ["1 Test Street"];
        if (name.Contains("name")) return [new string('A', 300), "Test Shopper"];
        return ["", "Test value"];
    }

    static List<string> SearchTerms(HuntContext ctx)
    {
        var word = ctx.Oracles.ProductNames.Values.Select(n => n.Split(' ')[0]).FirstOrDefault(w => w.Length >= 4 && char.IsUpper(w[0]));
        // A search with no results first, then the other letter case, and an ordinary search last (it leaves results on screen).
        return word is null ? ["zzzz-no-match", "test"] : ["zzzz-no-match", word.ToUpperInvariant(), word.ToLowerInvariant()];
    }

    [GeneratedRegex(@"/(?:[A-Za-z]{1,4}-)?\d+(?=/|$)")] private static partial Regex IdSegment();
    [GeneratedRegex("checkout|proceed|cart|basket|bag|continue|next", RegexOptions.IgnoreCase)] private static partial Regex Progress();
    [GeneratedRegex(@"\b[A-Z]{3,}\d{1,3}\b")] private static partial Regex CouponHint();
    [GeneratedRegex(@"[Oo]nly (\d+) left|(\d+) in stock")] private static partial Regex StockHint();
    [GeneratedRegex(@"\bdate\b|check-?in|check-?out|arrival|departure|yyyy|dd/mm|mm/dd", RegexOptions.IgnoreCase)] private static partial Regex DateField();
}
