using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BugHunter.Api.Hunter;

// Deterministic oracles: each one fires only on evidence the browser recorded (page text, console, network JSON).
// They are app-agnostic heuristics (money arithmetic, validation, consistency), not knowledge of a specific site.
public class OracleState
{
    public Dictionary<string, string> ProductNames { get; } = new(); // id -> name, from any JSON list of named items
    public Dictionary<string, int> Stock { get; } = new();           // id -> last seen available quantity
    public HashSet<string> Keys { get; } = [];
    public HashSet<decimal> TaxShort { get; } = [];                  // taxable amounts whose tax came out one cent short
    public List<(HuntStep Step, NetworkEvent Event)> History { get; } = []; // API calls in order, for rules added later
    public Dictionary<string, string> Samples { get; } = new();
    public Twin.PredictionState Predicted { get; } = new();          // what the twin's predictions compare against
    public List<(string Name, string Description)> Catalog { get; } = []; // demo shop products, for benchmark scoring only      // "METHOD /path" -> a compact example of its JSON, for the model
}

public static partial class Oracles
{
    public record Hit(string Key, string Oracle, string Title, string Severity, string Expected, string Actual, string? Evidence = null,
        string? Method = null, string? Path = null, string? AssertText = null, string? Url = null);

    static readonly string[] MutatingMethods = ["POST", "PUT", "PATCH", "DELETE"];

    public static List<Hit> Evaluate(OracleState st, PageSnapshot snap, List<Hit>? repeats = null)
    {
        var hits = new List<Hit>();
        // Chrome logs every 4xx/5xx resource as a console error; HTTP failures have their own oracles, so only real script errors count here.
        foreach (var c in snap.Console.Where(c => !c.Text.StartsWith("Failed to load resource", StringComparison.Ordinal)))
            hits.Add(new($"js-error:{Norm(c.Text)}", "js-error", "JavaScript error on the page", "Medium",
                "The page runs without uncaught errors.", c.Text, $"{c.Kind} at {c.Url}"));

        foreach (var n in snap.Network)
        {
            if (n.Status >= 500)
                hits.Add(new($"server-error:{n.Method}:{n.Path}", "server-error", $"Server error on {n.Method} {n.Path}", "High",
                    "The server handles the request without a 5xx error.", $"HTTP {n.Status}", Trim(n.Body), n.Method, n.Path));
            else if (n.Status == 404 && n.ResourceType == "document")
                hits.Add(new($"broken-link:{n.Path}", "broken-link", $"Broken link: {n.Path}", "Low", "Links lead to existing pages.", "HTTP 404", null, n.Method, n.Path));
            Network(st, n, hits);
        }

        foreach (Match m in BrokenRender().Matches(snap.Text))
            hits.Add(new($"broken-render:{m.Value}:{PathOf(snap.Url)}", "broken-render", $"Page shows '{m.Value}'", "Medium",
                "All values on the page render as real values.", $"The page text contains '{m.Value}': …{Around(snap.Text, m.Index)}…", AssertText: m.Value));
        foreach (Match m in NegativeMoney().Matches(snap.Text))
            hits.Add(new("negative-amount", "negative-amount", "Negative money amount shown", "High",
                "Prices and totals are never negative.", $"…{Around(snap.Text, m.Index)}…", AssertText: m.Value));

        var fresh = new List<Hit>();
        foreach (var h in hits) if (st.Keys.Add(h.Key)) fresh.Add(h with { Url = snap.Url }); else repeats?.Add(h);
        return fresh;
    }

    static void Network(OracleState st, NetworkEvent n, List<Hit> hits)
    {
        var body = n.Json();
        var ok = n.Status is >= 200 and < 300;
        var mutating = MutatingMethods.Contains(n.Method);

        // Accepted input that a validator should have rejected. Values filled from secrets arrive masked and are skipped.
        if (ok && mutating && n.RequestJson() is JsonObject req)
            foreach (var (k, v) in Flatten(req))
                if (InvalidInput(k, v) is { } why)
                    hits.Add(new(why.Key, "invalid-input-accepted", why.Title, why.Severity, why.Expected,
                        $"{n.Method} {n.Path} with {k} = \"{v}\" returned HTTP {n.Status}.", Trim(n.RequestBody), n.Method, n.Path));

        if (body is null) return;
        if (ok && n.Method == "GET") Learn(st, body);

        // Search consistency: a known item whose name contains the query is missing from the results.
        if (ok && n.Method == "GET" && Query(n.Url) is { Length: > 0 } q && Items(body) is { } results && st.ProductNames.Count > 0)
        {
            var got = results.Select(r => Id(r) ?? Name(r)).ToHashSet();
            var missed = st.ProductNames.Where(p => p.Value.Contains(q, StringComparison.OrdinalIgnoreCase) && !got.Contains(p.Key) && !got.Contains(p.Value)).Select(p => p.Value).ToList();
            if (missed.Count > 0)
                hits.Add(new("search-miss", "search-miss", "Search misses matching products", "High",
                    $"Searching for \"{q}\" returns every product whose name contains it, regardless of letter case.",
                    $"Results for \"{q}\" omit: {string.Join(", ", missed.Take(5))}.", Trim(n.Body), n.Method, n.Path, AssertText: missed[0]));
        }

        foreach (var o in Objects(body)) Totals(st, o, n, hits);

        // Duplicate promotions: the same coupon/promo code applied more than once.
        foreach (var (name, arr) in Arrays(body).Where(a => PromoName().IsMatch(a.Name)))
        {
            var codes = arr.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? CodeOf(s) : null).Where(s => s is not null).ToList();
            var dup = codes.GroupBy(c => c).FirstOrDefault(g => g.Count() > 1);
            if (dup is not null)
                hits.Add(new("duplicate-promotion", "duplicate-promotion", "Same coupon applied more than once", "High",
                    "A coupon can be applied only once per order; re-applying it is rejected or ignored.",
                    $"'{name}' lists {dup.Key} {dup.Count()} times after {n.Method} {n.Path}.", Trim(n.Body), n.Method, n.Path));
        }

        // Overselling: an order accepted for more units than the stock last shown.
        if (ok && mutating && OrderPath().IsMatch(n.Path))
            foreach (var line in Objects(body).Where(o => Num(o, "qty", "quantity") is not null && (Str(o, "productId", "sku", "itemId") is not null)))
            {
                var pid = Str(line, "productId", "sku", "itemId")!; var qty = (int)Num(line, "qty", "quantity")!.Value;
                if (st.Stock.TryGetValue(pid, out var have) && qty > have)
                    hits.Add(new("oversell", "oversell", "Order accepted for more units than in stock", "High",
                        $"Ordering {qty} × {st.ProductNames.GetValueOrDefault(pid, pid)} is rejected because only {have} are in stock.",
                        $"{n.Method} {n.Path} returned HTTP {n.Status} and created the order.", Trim(n.Body), n.Method, n.Path));
            }
    }

    // Money arithmetic over any object that looks like a cart or an order.
    static void Totals(OracleState st, JsonObject o, NetworkEvent n, List<Hit> hits)
    {
        var lines = (o["items"] ?? o["lines"]) as JsonArray;
        foreach (var l in lines?.OfType<JsonObject>() ?? [])
        {
            var price = Num(l, "price", "unitPrice"); var qty = Num(l, "qty", "quantity"); var lt = Num(l, "lineTotal", "total", "amount");
            if (qty is <= 0)
                hits.Add(new("non-positive-qty", "non-positive-qty", "Cart accepts a zero or negative quantity", "High",
                    "Quantities below 1 are rejected or remove the line.", $"Line '{Name(l)}' has quantity {qty} after {n.Method} {n.Path}.", Trim(n.Body), n.Method, n.Path));
            if (price is not null && qty is not null && lt is not null && Math.Abs(price.Value * qty.Value - lt.Value) > 0.005m)
                hits.Add(new($"line-total:{Name(l)}", "cart-arithmetic", "Line total does not equal price × quantity", "High",
                    $"{Money(price)} × {qty} = {Money(price * qty)}", $"Line total is {Money(lt)}.", Trim(n.Body), n.Method, n.Path));
        }
        var subtotal = Num(o, "subtotal"); var total = Num(o, "total");
        if (subtotal is null || total is null) return;
        var discount = Num(o, "discount") ?? 0; var shipping = Num(o, "shipping") ?? 0; var tax = Num(o, "tax") ?? 0;
        if (lines is not null)
        {
            var sum = lines.OfType<JsonObject>().Sum(l => Num(l, "lineTotal") ?? (Num(l, "price") * Num(l, "qty")) ?? 0);
            if (Math.Abs(sum - subtotal.Value) > 0.005m)
                hits.Add(new("subtotal-mismatch", "cart-arithmetic", "Subtotal does not equal the sum of the lines", "High", $"Sum of lines = {Money(sum)}", $"Subtotal is {Money(subtotal)}.", Trim(n.Body), n.Method, n.Path));
        }
        if (total < 0 || subtotal < 0)
            hits.Add(new("negative-amount", "negative-amount", "Negative money amount", "High", "Totals are never negative.", $"subtotal {Money(subtotal)}, total {Money(total)} after {n.Method} {n.Path}.", Trim(n.Body), n.Method, n.Path));
        if (subtotal >= 0 && discount > subtotal + 0.005m)
            hits.Add(new("discount-exceeds-subtotal", "cart-arithmetic", "Discount is larger than the subtotal", "High", $"Discount ≤ subtotal ({Money(subtotal)}).", $"Discount is {Money(discount)}.", Trim(n.Body), n.Method, n.Path));
        var expect = subtotal.Value - discount + shipping + tax;
        if (Math.Abs(expect - total.Value) > 0.015m)
            hits.Add(new("total-mismatch", "cart-arithmetic", "Total does not add up", "High", $"subtotal − discount + shipping + tax = {Money(expect)}", $"Total is {Money(total)}.", Trim(n.Body), n.Method, n.Path));

        // Tax rounding: infer the rate (to 1/8 of a percent) and check the cents. One cent short twice in a row means truncation.
        var taxable = subtotal.Value - discount;
        if (taxable >= 10 && tax > 0)
        {
            var rate = Math.Round(tax / taxable / 0.00125m) * 0.00125m;
            var rounded = Math.Round(taxable * rate, 2, MidpointRounding.AwayFromZero);
            if (rounded - tax is >= 0.005m and <= 0.0101m && st.TaxShort.Add(taxable) && st.TaxShort.Count >= 2)
                hits.Add(new("tax-rounding", "tax-rounding", "Tax is truncated instead of rounded", "Medium",
                    $"Tax at {rate:P2} on {Money(taxable)} rounds to {Money(rounded)}.", $"Tax is {Money(tax)} (one cent short), seen on {st.TaxShort.Count} different carts.", Trim(n.Body), n.Method, n.Path));
        }
    }

    record Invalid(string Key, string Title, string Severity, string Expected);

    static Invalid? InvalidInput(string key, string value)
    {
        if (value.Contains("{{secret:")) return null;
        var k = key.ToLowerInvariant();
        if (Regex.IsMatch(k, "exp") && Regex.Match(value.Trim(), @"^(0[1-9]|1[0-2])\s*/\s*(\d{2}|\d{4})$") is { Success: true } m)
        {
            var y = int.Parse(m.Groups[2].Value); if (y < 100) y += 2000;
            if (new DateTime(y, int.Parse(m.Groups[1].Value), 1).AddMonths(1) <= DateTime.UtcNow)
                return new("invalid-input:expired-card", "Expired card accepted at checkout", "High", "Payment with a card whose expiry date has passed is rejected.");
        }
        if (Regex.IsMatch(k, "card.?num|cardnumber|^pan$") && Regex.Replace(value, @"[\s-]", "") is var digits && Regex.IsMatch(digits, @"^\d{12,19}$") && !Luhn(digits))
            return new("invalid-input:card-number", "Invalid card number accepted", "High", "Card numbers that fail the Luhn check are rejected.");
        // Zero is a legitimate "remove this line"; a zero line that stays in the cart is caught by Totals.
        if (Regex.IsMatch(k, "^(qty|quantity)$") && decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var q) && q < 0)
            return new("non-positive-qty", "Cart accepts a zero or negative quantity", "High", "Negative quantities are rejected.");
        if (Regex.IsMatch(k, "email") && value.Length > 0 && !Regex.IsMatch(value, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
            return new("invalid-input:email", "Malformed email address accepted", "Medium", "Malformed email addresses are rejected.");
        return null;
    }

    static void Learn(OracleState st, JsonNode body)
    {
        foreach (var o in Objects(body))
        {
            if (Id(o) is not { } id) continue;
            if (o["name"] is JsonValue nv && nv.TryGetValue<string>(out var name) && name.Length > 0 && o["price"] is not null) st.ProductNames[id] = name;
            if (Num(o, "stock", "inventory", "available", "quantityAvailable") is { } s) st.Stock[id] = (int)s;
        }
    }

    static IEnumerable<KeyValuePair<string, string>> Flatten(JsonObject o)
    {
        foreach (var (k, v) in o)
        {
            if (v is JsonObject child) { foreach (var kv in Flatten(child)) yield return kv; }
            else if (v is JsonValue val) yield return new(k, val.ToString());
        }
    }

    static IEnumerable<JsonObject> Objects(JsonNode? n)
    {
        if (n is JsonObject o) { yield return o; foreach (var (_, v) in o) foreach (var c in Objects(v)) yield return c; }
        else if (n is JsonArray a) foreach (var v in a) foreach (var c in Objects(v)) yield return c;
    }

    static IEnumerable<(string Name, JsonArray Arr)> Arrays(JsonNode? n)
    {
        foreach (var o in Objects(n)) foreach (var (k, v) in o) if (v is JsonArray a) yield return (k, a);
    }

    static List<JsonObject>? Items(JsonNode body) =>
        body is JsonArray a ? a.OfType<JsonObject>().ToList() : (body["items"] ?? body["results"] ?? body["products"] ?? body["data"]) is JsonArray i ? i.OfType<JsonObject>().ToList() : null;

    static string? Query(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        foreach (var part in u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0] is "q" or "query" or "search" or "term" or "keyword") return Uri.UnescapeDataString(kv[1].Replace('+', ' ')).Trim();
        }
        return null;
    }

    static string? Id(JsonObject o) => Str(o, "id", "productId", "sku");
    static string Name(JsonObject o) => Str(o, "name", "title") ?? "item";
    static string? Str(JsonObject o, params string[] keys) => keys.Select(k => o[k]).OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).FirstOrDefault(s => s is not null);
    static decimal? Num(JsonObject o, params string[] keys)
    {
        foreach (var k in keys)
            if (o[k] is JsonValue v)
            {
                if (v.TryGetValue<decimal>(out var d)) return d;
                if (v.TryGetValue<double>(out var f)) return (decimal)f;
                if (v.TryGetValue<int>(out var i)) return i;
            }
        return null;
    }

    static string? CodeOf(string s) => Regex.Match(s, @"^\s*([A-Za-z0-9_-]+)").Groups[1].Value is { Length: > 0 } c ? c.ToUpperInvariant() : null;
    static bool Luhn(string n) { var sum = 0; var alt = false; for (var i = n.Length - 1; i >= 0; i--) { var d = n[i] - '0'; if (alt) { d *= 2; if (d > 9) d -= 9; } sum += d; alt = !alt; } return sum % 10 == 0; }
    static string Money(decimal? d) => d is null ? "?" : d.Value.ToString("$0.00", CultureInfo.InvariantCulture);
    static string Norm(string s) { var r = Regex.Replace(s, @"\d+", "#"); return r[..Math.Min(80, r.Length)]; }
    static string PathOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsolutePath + u.Fragment.Split('?')[0] : url;
    static string Around(string text, int i) { var s = Math.Max(0, i - 50); var e = Math.Min(text.Length, i + 50); return text[s..e].Replace('\n', ' '); }
    static string? Trim(string? s) => s is null ? null : s.Length > 1500 ? s[..1500] + "…" : s;

    [GeneratedRegex(@"\bNaN\b|\bundefined\b|\[object Object\]|\bInfinity\b")] private static partial Regex BrokenRender();
    [GeneratedRegex(@"\$\s?-\d|(?<![\w)])-\$\d")] private static partial Regex NegativeMoney();
    [GeneratedRegex("coupon|promo|voucher", RegexOptions.IgnoreCase)] private static partial Regex PromoName();
    [GeneratedRegex("checkout|order|purchase|payment", RegexOptions.IgnoreCase)] private static partial Regex OrderPath();
}
