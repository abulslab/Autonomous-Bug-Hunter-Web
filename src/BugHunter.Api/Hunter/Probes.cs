using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BugHunter.Api.Hunter;

// One request the engine sends on its own: a recorded API call resent with a change (a boundary value, an invalid
// format, a repeat, no session). Probes need no model and take seconds, so finding a bug does not depend on whether
// the AI happened to click the right thing.
public record Probe(string Group, string Kind, string Label, NetworkEvent Base, int BaseStep, string? Body, string? Url = null,
    bool DropAuth = false, int Times = 1, Probe? Setup = null, string? Requirement = null, string? Why = null, string Source = "Engine", string? Method = null);
// Method: send with another method than the base call's (a probe that borrows a recorded call's session for another endpoint).

public static partial class Probes
{
    public const int MaxRequests = 80;

    // Paths that sign in or out: a request without a session is expected to work there.
    [GeneratedRegex(@"login|logout|signin|signout|sign-in|sign-out|register|signup|password|session|token", RegexOptions.IgnoreCase)] private static partial Regex AuthPath();
    // Calls that place an order or take payment: probed only when the hunt allows irreversible actions.
    [GeneratedRegex(@"checkout|order|purchase|payment|pay\b", RegexOptions.IgnoreCase)] private static partial Regex OrderPath();
    [GeneratedRegex(@"/(\d+|[A-Z]+-\d+|p-\d+)(?=/|$)")] private static partial Regex IdSegment();

    public static string Endpoint(NetworkEvent e) => $"{e.Method} {FoldIds(e.Path)}";
    // /api/bookings/B-1001/cancel and /api/bookings/{id}/cancel are the same endpoint.
    public static string FoldIds(string path) => IdSegment().Replace(path, "/{id}");
    // The ids in a path, in order: /api/bookings/B-1001/cancel -> [B-1001].
    public static List<string> Ids(string path) => IdSegment().Matches(path.Split('?')[0]).Select(m => m.Groups[1].Value).ToList();

    // The latest recorded call per endpoint, preferring one that succeeded (a valid baseline makes each change the only fault).
    public static List<(HuntStep Step, NetworkEvent Event)> Bases(OracleState st) =>
        st.History.Where(x => x.Event.Rid is not null && x.Event.Status != 0 && x.Event.ResourceType is "xhr" or "fetch")
            .GroupBy(x => Endpoint(x.Event))
            .Select(g => g.LastOrDefault(x => x.Event.Status is >= 200 and < 300) is { Event: not null } ok ? ok : g.Last())
            .ToList();

    public static List<Probe> Plan(Hunt h, OracleState st)
    {
        var list = new List<Probe>();
        var bases = Bases(st);
        // An order needs something in the cart: before each order probe, resend the last successful add-to-cart.
        var add = bases.Where(b => b.Event.Method == "POST" && b.Event.Status is >= 200 and < 300 && Regex.IsMatch(b.Event.Path, "cart|basket|bag", RegexOptions.IgnoreCase)
            && b.Event.RequestJson() is JsonObject o && Keys(o).Any(k => Regex.IsMatch(k, "^(qty|quantity)$", RegexOptions.IgnoreCase))).Select(b => ((HuntStep, NetworkEvent)?)b).FirstOrDefault();
        Probe? Refill(string g) => add is { } a ? new Probe(g, "setup", "put an item back in the cart", a.Item2, a.Item1.N, null) : null;
        var g = 0;

        // 1. Search and filters: the same query in other letter cases.
        foreach (var (step, e) in bases.Where(b => b.Event.Method == "GET"))
            foreach (var (k, v) in Query(e.Url).Where(q => Regex.IsMatch(q.Value, "[A-Za-z]")))
            {
                var grp = $"A{++g}";
                foreach (var variant in new[] { v.ToLowerInvariant(), v.ToUpperInvariant(), char.ToUpperInvariant(v[0]) + v[1..].ToLowerInvariant() }.Distinct())
                    list.Add(new(grp, "case", $"{k}=\"{variant}\"", e, step.N, null, WithQuery(e.Url, k, variant)));
            }

        foreach (var (step, e) in bases.Where(b => b.Event.Method is "POST" or "PUT" or "PATCH" && b.Event.RequestJson() is JsonObject && !AuthPath().IsMatch(b.Event.Path)))
        {
            var body = (JsonObject)e.RequestJson()!;
            var order = OrderPath().IsMatch(e.Path);
            if (order && !h.AllowIrreversible) continue;
            Probe With(string kind, string key, string value, string label) =>
                new($"A{++g}", kind, label, e, step.N, Set(body, key, value), Setup: order ? Refill($"A{g}") : null);

            // 2. The same submission twice (a coupon, a vote, a form). Orders are excluded: two orders are legitimate.
            if (!order) list.Add(new($"A{++g}", "repeat", "send the same request twice", e, step.N, null, Times: 2));

            foreach (var key in Keys(body))
            {
                var v = Value(body, key);
                if (v is null || v.Contains("{{secret:")) continue;
                if (IsNumber(body, key))
                {
                    var qty = Regex.IsMatch(key, "^(qty|quantity|count|amount|units)$", RegexOptions.IgnoreCase);
                    foreach (var bad in new[] { "-1", "0", "99999" }) list.Add(With("boundary", key, bad, $"{key} = {bad}"));
                    if (qty)
                    {
                        // One more than the stock shown for this product, then (when allowed) an order with it in the cart.
                        var pid = Keys(body).Where(k => Regex.IsMatch(k, "^(productId|sku|itemId|id)$", RegexOptions.IgnoreCase)).Select(k => Value(body, k)).FirstOrDefault(x => x is not null)
                            ?? e.Path.Split('/').LastOrDefault(st.Stock.ContainsKey);
                        if (pid is not null && st.Stock.TryGetValue(pid, out var have))
                        {
                            var over = With("stock", key, (have + 1).ToString(), $"{key} = {have + 1} (stock is {have})");
                            list.Add(over);
                            if (bases.FirstOrDefault(b => b.Event.Method == "POST" && OrderPath().IsMatch(b.Event.Path) && b.Event.RequestJson() is JsonObject) is { Event: not null } ord && h.AllowIrreversible)
                                list.Add(new(over.Group, "stock", "place the order with that quantity", ord.Event, ord.Step.N, null));
                        }
                        // Several ordinary quantities: different totals exercise the arithmetic, tax and rule checks.
                        foreach (var ok in new[] { "2", "3", "5", "7" }) list.Add(With("sweep", key, ok, $"{key} = {ok}"));
                    }
                    continue;
                }
                if (Regex.IsMatch(key, "exp", RegexOptions.IgnoreCase) && Regex.IsMatch(v, @"^\d{1,2}\s*/\s*\d{2,4}$")) list.Add(With("invalid", key, "01/20", $"{key} = 01/20 (in the past)"));
                if (Regex.IsMatch(key, "card|pan", RegexOptions.IgnoreCase) && Regex.Replace(v, @"[\s-]", "") is var digits && Regex.IsMatch(digits, @"^\d{12,19}$"))
                    list.Add(With("invalid", key, digits[..^1] + (char)('0' + (digits[^1] - '0' + 1) % 10), $"{key} fails the Luhn check"));
                if (Regex.IsMatch(key, "email", RegexOptions.IgnoreCase)) list.Add(With("invalid", key, "not-an-email", $"{key} = not-an-email"));
                if (Regex.IsMatch(key, "zip|postal|postcode", RegexOptions.IgnoreCase)) list.Add(With("invalid", key, "ABCDE", $"{key} = ABCDE"));
            }

            // 3. No session: a call that changes data should be refused when nobody is signed in.
            list.Add(new($"A{++g}", "no-session", "send without the session", e, step.N, null, DropAuth: true));
        }
        return list;
    }

    // A change the model proposed: set one field of a recorded call to each value. Only fields the call really sends.
    public static List<Probe> FromModel(Hunt h, OracleState st, string on, string field, IEnumerable<string> values, string? why, string? requirement, ref int g)
    {
        var list = new List<Probe>();
        var rule = new Invariant { On = on };
        var b = Bases(st).LastOrDefault(x => InvariantRules.Applies(rule, x.Event));
        if (b.Event is null || b.Event.Method is "GET" or "DELETE" || b.Event.RequestJson() is not JsonObject body || !Keys(body).Contains(field)) return list;
        if (OrderPath().IsMatch(b.Event.Path) && !h.AllowIrreversible) return list;
        var grp = $"M{++g}";
        foreach (var v in values.Where(v => v.Length <= 200).Distinct().Take(6))
            list.Add(new(grp, "model", $"{field} = {v}", b.Event, b.Step.N, Set(body, field, v), Requirement: requirement, Why: why, Source: "Model"));
        return list;
    }

    // Field names anywhere in a JSON body (nested objects included).
    public static List<string> Keys(JsonObject o) =>
        o.SelectMany(kv => kv.Value is JsonObject c ? Keys(c) : kv.Value is JsonValue ? [kv.Key] : []).Distinct().ToList();

    // Only JSON numbers get number probes: digits in a string (a card number, a postcode) are formats, not amounts.
    static bool IsNumber(JsonObject o, string key)
    {
        foreach (var (k, v) in o)
        {
            if (k == key && v is JsonValue jv) return jv.GetValueKind() == System.Text.Json.JsonValueKind.Number;
            if (v is JsonObject c && IsNumber(c, key)) return true;
        }
        return false;
    }

    static string? Value(JsonObject o, string key)
    {
        foreach (var (k, v) in o)
        {
            if (k == key && v is JsonValue jv) return jv.ToString();
            if (v is JsonObject c && Value(c, key) is { } inner) return inner;
        }
        return null;
    }

    // A copy of the body with one field changed; numbers stay numbers when the new value is numeric.
    public static string Set(JsonObject body, string key, string value)
    {
        var copy = (JsonObject)body.DeepClone();
        void Walk(JsonObject o)
        {
            foreach (var k in o.Select(kv => kv.Key).ToList())
            {
                if (k == key && o[k] is JsonValue old)
                    o[k] = InvariantRules.Number(old) is not null && decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var d)
                        ? JsonValue.Create(d) : JsonValue.Create(value);
                else if (o[k] is JsonObject c) Walk(c);
            }
        }
        Walk(copy);
        return copy.ToJsonString();
    }

    static List<KeyValuePair<string, string>> Query(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
            ? u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
                .Where(p => p.Length == 2 && p[1].Length > 1).Select(p => new KeyValuePair<string, string>(Uri.UnescapeDataString(p[0]), Uri.UnescapeDataString(p[1].Replace('+', ' ')))).ToList()
            : [];

    static string WithQuery(string url, string key, string value)
    {
        var u = new UriBuilder(url);
        u.Query = string.Join("&", u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => Uri.UnescapeDataString(p.Split('=', 2)[0]) == key ? $"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}" : p));
        return u.Uri.ToString();
    }

    // Results in a JSON list response, for comparing case variants.
    public static int? Count(NetworkEvent e) =>
        e.Json() switch { JsonArray a => a.Count, JsonObject o when (o["items"] ?? o["results"] ?? o["products"] ?? o["data"]) is JsonArray i => i.Count, _ => null };
}
