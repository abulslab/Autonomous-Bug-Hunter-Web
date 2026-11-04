using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BugHunter.Api.Common;
using BugHunter.Api.Hunter;
using BugHunter.Api.Projects;

namespace BugHunter.Api.Twin;

// The predictive twin: what the app should do, checked on every recorded response. Each prediction comes from intent,
// never from how the app behaved before (principle 2 of the plan):
//   lifecycle    a transition only works from the states the twin's lifecycle allows (from the stories, via P4)
//   permission   an endpoint a project role declares as its own answers nobody else (the project's configuration)
//   consistency  the app agrees with itself: a price or rate an item is listed at is what it is charged at
// Field relations (the twin's rules) run through the existing rule engine as hunt rules.
public class PredictionSet
{
    public string? Role { get; set; }
    public List<LifecyclePrediction> Lifecycles { get; set; } = [];
    public List<ExclusivePrediction> Exclusive { get; set; } = [];
    public bool Consistency { get; set; } = true;

    public static PredictionSet For(Project p, AppTwin? twin, string? role) => new()
    {
        Role = role,
        Lifecycles = twin?.Lifecycles.SelectMany(l => l.Transitions.Where(t => t.Endpoint.Contains("{id}")).GroupBy(t => t.Endpoint)
            .Select(g => new LifecyclePrediction(g.Key, l.Entity, l.Field, g.Select(t => t.From).Where(f => f.Length > 0).Distinct().ToList(), g.First().To))).ToList() ?? [],
        Exclusive = p.Roles.SelectMany(r => r.Exclusive.Select(x => new ExclusivePrediction(r.Id, r.Name, x))).ToList(),
    };
}

public record LifecyclePrediction(string Endpoint, string Entity, string Field, List<string> From, string To);
public record ExclusivePrediction(string Role, string RoleName, string PathPrefix);

public record PredictionHit(string Kind, string Key, string Title, string Expected, string Actual, string Source, string? Cause = null);

// What the hunt has seen so far that predictions compare against: entity states and listed values by id.
public class PredictionState
{
    public Dictionary<string, string> States { get; } = new();                                   // entity id -> its status value
    public Dictionary<(string Id, string Field), (decimal Value, string Where)> Listed { get; } = new();
}

public static partial class Predictions
{
    static bool Ok(NetworkEvent e) => e.Status is >= 200 and < 300;
    static bool Priced(string field) => PriceField().IsMatch(field);

    public static List<PredictionHit> Check(PredictionSet ps, PredictionState st, NetworkEvent e, bool dropAuth)
    {
        var hits = new List<PredictionHit>();
        if (e.ResourceType is not ("xhr" or "fetch") || e.Status == 0) return hits;
        var key = Probes.Endpoint(e);
        var body = e.Json();

        // Permission: another role's endpoint answered this role (or a call with no session).
        if (Ok(e) && !AuthPath().IsMatch(e.Path))
        {
            var who = dropAuth ? null : ps.Role;
            var owners = ps.Exclusive.Where(x => e.Path.StartsWith(x.PathPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
            if (owners.Count > 0 && owners.All(o => o.Role != who))
                hits.Add(new("permission", $"prediction:permission:{key}", $"{key} answers {(who is null ? "a call without a session" : $"the {who} role")}",
                    $"Only {string.Join(" or ", owners.Select(o => o.RoleName))} can use {key}.",
                    $"It returned HTTP {e.Status} {(who is null ? "without a session" : $"as {who}")}.", "project roles",
                    $"permission:{who ?? "no-session"}:{owners[0].PathPrefix}")); // every endpoint of the area is the same bug
        }

        // Lifecycle: a transition on an entity in a state it should not leave that way.
        if (Ok(e) && ps.Lifecycles.FirstOrDefault(l => l.Endpoint == key) is { } lp && lp.From.Count > 0 && IdOf(e.Path) is { } id
            && st.States.TryGetValue(id, out var prior) && !lp.From.Contains(prior, StringComparer.OrdinalIgnoreCase))
            hits.Add(new("lifecycle", $"prediction:lifecycle:{key}", $"{lp.Entity} moved from {prior} by {key}",
                $"{key} moves a {lp.Entity} only from {string.Join(" or ", lp.From)} (to {lp.To}).",
                $"{lp.Entity} {id} was {prior} and the call returned HTTP {e.Status}.", "twin lifecycle"));

        // Consistency: an item charged at a different price or rate than it was listed at.
        if (ps.Consistency && Ok(e) && body is not null && !(e.Method == "GET" && IsList(body)))
            foreach (var o in Objects(body))
                foreach (var (refKey, refVal) in o.Where(kv => kv.Key.EndsWith("Id") && kv.Key.Length > 2 && kv.Value is JsonValue))
                {
                    var refId = refVal!.ToString();
                    foreach (var (field, v) in o.Where(kv => Priced(kv.Key)))
                        if (InvariantRules.Number(v) is decimal here && st.Listed.TryGetValue((refId, field), out var listed) && here != listed.Value)
                            hits.Add(new("consistency", $"prediction:consistency:{key}:{field}", $"{field} of {refId} differs from its listing",
                                $"{refId} is charged the {field} it is listed at ({listed.Value} in {listed.Where}).",
                                $"{key} shows {field} = {here} for {refId}.", "the app's own listing"));
                }

        // Then learn from this response: listed values (from lists) and entity states.
        if (Ok(e) && body is not null)
        {
            if (e.Method == "GET" && IsList(body))
                foreach (var o in Objects(body).Where(o => o["id"] is JsonValue))
                    foreach (var (field, v) in o.Where(kv => Priced(kv.Key)))
                        if (InvariantRules.Number(v) is decimal n) st.Listed[(o["id"]!.ToString(), field)] = (n, key);
            var fields = ps.Lifecycles.Select(l => l.Field).Append("status").Distinct().ToList();
            foreach (var o in Objects(body).Where(o => o["id"] is JsonValue))
                foreach (var f in fields)
                    if (o[f] is JsonValue sv && sv.TryGetValue<string>(out var state)) st.States[o["id"]!.ToString()] = state;
        }
        return hits.DistinctBy(h => h.Key).ToList();
    }

    public static string? IdOf(string path) => IdValue().Match(path) is { Success: true } m ? m.Groups[1].Value : null;

    // A listing: a list of items that each have their own id (a cart's lines carry productId instead, so a cart is checked).
    static bool IsList(JsonNode body) => (body as JsonArray ?? body["items"] as JsonArray) is { Count: > 0 } items && items[0] is JsonObject first && first["id"] is JsonValue;

    static IEnumerable<JsonObject> Objects(JsonNode? n)
    {
        if (n is JsonObject o) { yield return o; foreach (var (_, v) in o) foreach (var c in Objects(v)) yield return c; }
        else if (n is JsonArray a) foreach (var v in a) foreach (var c in Objects(v)) yield return c;
    }

    // ---- probes the predictions make possible ----

    // The most recent successful call, preferring the given method: its session is the current one (an early call may
    // predate signing in).
    static (HuntStep Step, NetworkEvent Event) Latest(List<(HuntStep Step, NetworkEvent Event)> bases, string method) =>
        bases.Where(x => x.Event.Status is >= 200 and < 300).OrderByDescending(x => x.Event.Method == method).ThenByDescending(x => x.Step.N).FirstOrDefault();

    // A transition tried on an entity in a state the lifecycle does not allow it from (a delivered order cancelled).
    public static List<Probe> LifecycleProbes(PredictionSet ps, PredictionState st, List<(HuntStep Step, NetworkEvent Event)> bases)
    {
        var list = new List<Probe>();
        foreach (var lp in ps.Lifecycles.Where(l => l.From.Count > 0))
        {
            var method = lp.Endpoint.Split(' ')[0]; var template = lp.Endpoint.Split(' ', 2)[1];
            var b = Latest(bases, method);
            if (b.Event is null) continue;
            foreach (var (id, state) in st.States.Where(s => !lp.From.Contains(s.Value, StringComparer.OrdinalIgnoreCase) && !s.Value.Equals(lp.To, StringComparison.OrdinalIgnoreCase)).Take(2))
                list.Add(new Probe($"L{list.Count + 1}", "lifecycle", $"{lp.Entity} {id} is {state}", b.Event, b.Step.N, null, template.Replace("{id}", id),
                    Method: method, Why: $"{lp.Endpoint} should only work from {string.Join(" or ", lp.From)}", Source: "Twin"));
        }
        return list;
    }

    // Another role's endpoints called with this role's session (a link the UI hides is still an open door).
    public static List<Probe> CrossRoleProbes(PredictionSet ps, AppTwin? twin, List<(HuntStep Step, NetworkEvent Event)> bases)
    {
        var list = new List<Probe>();
        var b = Latest(bases, "GET");
        if (b.Event is null || twin is null) return list;
        foreach (var x in ps.Exclusive.Where(x => x.Role != ps.Role))
            foreach (var ep in twin.Endpoints.Where(e => e.Method == "GET" && !e.Path.Contains("{id}") && e.Path.StartsWith(x.PathPrefix, StringComparison.OrdinalIgnoreCase) && !AuthPath().IsMatch(e.Path)).Take(3))
                list.Add(new Probe($"X{list.Count + 1}", "cross-role", $"as {ps.Role ?? "this role"}", b.Event, b.Step.N, null, ep.Path, Method: "GET",
                    Why: $"{ep.Key} belongs to {x.RoleName}", Source: "Twin"));
        return list;
    }

    // The same write for other listed items: a request naming an item by id, resent with each other id the app listed.
    public static List<Probe> OtherItemProbes(PredictionState st, List<(HuntStep Step, NetworkEvent Event)> bases)
    {
        var list = new List<Probe>();
        var ids = st.Listed.Keys.Select(k => k.Id).Distinct().ToList();
        foreach (var (step, e) in bases.Where(x => x.Event.Method is "POST" or "PUT" && x.Event.Status is >= 200 and < 300))
            if (e.RequestJson() is JsonObject req)
                foreach (var (field, v) in req.Where(kv => kv.Key.EndsWith("Id") && kv.Value is JsonValue && ids.Contains(kv.Value!.ToString())).Take(1))
                    foreach (var other in ids.Where(i => i != v!.ToString()).Take(12))
                    {
                        var copy = req.DeepClone().AsObject(); copy[field] = other;
                        list.Add(new Probe($"I{list.Count + 1}", "other-item", $"{field} = {other}", e, step.N, copy.ToJsonString(),
                            Why: "the same request for another listed item", Source: "Twin"));
                    }
        return list;
    }

    [GeneratedRegex(@"price|rate|cost", RegexOptions.IgnoreCase)] private static partial Regex PriceField();
    [GeneratedRegex(@"login|logout|signin|signout|/me$", RegexOptions.IgnoreCase)] private static partial Regex AuthPath();
    [GeneratedRegex(@"/(\d+|[A-Z]+-\d+|p-\d+|[a-z]-\d+)(?=/|$)")] private static partial Regex IdValue();
}
