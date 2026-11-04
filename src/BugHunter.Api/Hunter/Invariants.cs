using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BugHunter.Api.Hunter;

// A rule that must always hold, written by the model (or the user) and checked by the engine against every API response
// the browser records. The model only proposes the rule; whether it held is decided deterministically on real evidence.
public class Invariant
{
    public string Id { get; set; } = "";
    public string On { get; set; } = "*";    // "METHOD /path" (method optional, * and {id} wildcards) or "*"
    public string Check { get; set; } = "";  // an expression in the small language below
    public string Claim { get; set; } = "";  // the rule in plain words
    public string? Requirement { get; set; } // the story key it comes from
    public string? Intent { get; set; }      // the intent whose success it checks
    public string Source { get; set; } = "Model"; // Model | User
    public int AddedAtStep { get; set; }
    public string Status { get; set; } = "Untested"; // Untested | Holding | Violated
    public bool Deferred { get; set; }       // admitted before its endpoint was recorded; checked from the first matching response
    public int Checks { get; set; }
    public int Holds { get; set; }
    public int Violations { get; set; }
    public string? FindingId { get; set; }
    [JsonIgnore] public bool Backfilled { get; set; }
}

public record HuntRequirement(string Key, string Title, string Criteria, string Source, int? Version = null); // Version: of the story, as the project's knowledge last saw it

public static partial class InvariantRules
{
    public const string Language = """
An invariant is {"id","on","check","claim","requirement"}:
- on: which API responses it applies to, "METHOD /path" with * and {id} as wildcards ("GET /api/cart", "POST /api/orders", "PUT /api/items/{id}"), or "*" for all. The path may be the end of the real path.
- check: an expression that must be true for every matching response.
  Names: fields of the response JSON directly (total, items[0].price), or response.x, request.x (the JSON the browser sent), status (HTTP code), query.x (URL query), prev.x (the previous state of the same thing: on a path with an id, such as /bookings/{id}/checkout, the entity with that id as the last earlier response showed it; on a path without one, the previous response on the same path).
  Operators: + - * / == != < <= > >= && || ! ( ), and/or/not also work. Strings in quotes.
  Functions: sum(list, expr), count(list), count(list, condition), all(list, condition), any(list, condition), min(list, expr), max(list, expr),
    abs(x), round(x, digits), len(x), lower(x), contains(text, part), exists(x), date(x), today(). Inside list functions, item fields are plain names.
  A check that refers to a field the response does not have is skipped for that response, not failed.
""";

    public static (Invariant? Rule, string? Error) Create(string? id, string? on, string? check, string? claim, string? requirement, string source, int step)
    {
        if (string.IsNullOrWhiteSpace(check)) return (null, "check is empty");
        if (check.Length > 400) return (null, "check is longer than 400 characters");
        if (Matcher(on) is null) return (null, $"on \"{on}\" is not \"METHOD /path\" or \"*\"");
        Expr e;
        try { e = Parser.Parse(check); }
        catch (FormatException ex) { return (null, $"check \"{check}\" does not parse: {ex.Message}"); }
        if (!e.Names().Any()) return (null, $"check \"{check}\" does not refer to any field");
        var slug = Regex.Replace((id ?? claim ?? check).ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return (new Invariant
        {
            Id = slug.Length == 0 ? "rule" : slug[..Math.Min(40, slug.Length)], On = string.IsNullOrWhiteSpace(on) ? "*" : on.Trim(), Check = check.Trim(),
            Claim = string.IsNullOrWhiteSpace(claim) ? check.Trim() : claim.Trim(), Requirement = string.IsNullOrWhiteSpace(requirement) ? null : requirement.Trim().ToUpperInvariant(),
            Source = source, AddedAtStep = step
        }, null);
    }

    // User rules, one per line: "GET /api/cart: total == subtotal + tax  # claim"
    public static (List<Invariant> Rules, List<string> Errors) ParseLines(string? text)
    {
        var rules = new List<Invariant>(); var errors = new List<string>();
        foreach (var raw in (text ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("//")))
        {
            var hash = raw.IndexOf(" #", StringComparison.Ordinal);
            var line = hash >= 0 ? raw[..hash].Trim() : raw; var claim = hash >= 0 ? raw[(hash + 2)..].Trim() : null;
            var colon = line.IndexOf(": ", StringComparison.Ordinal);
            var (r, err) = colon < 0 ? Create(null, "*", line, claim, null, "User", 0) : Create(null, line[..colon], line[(colon + 2)..], claim, null, "User", 0);
            if (r is null) errors.Add($"\"{raw}\": {err}");
            else { r.Id = Unique(rules, r.Id); rules.Add(r); }
        }
        return (rules, errors);
    }

    public static string Unique(IEnumerable<Invariant> existing, string id)
    {
        var taken = existing.Select(i => i.Id).ToHashSet();
        var x = id; for (var n = 2; taken.Contains(x); n++) x = $"{id}-{n}";
        return x;
    }

    public record Outcome(bool? Holds, Dictionary<string, string> Values);

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Expr> Parsed = new();

    public static bool Applies(Invariant r, NetworkEvent e) => Matcher(r.On) is { } m && m(e);

    // What "prev" means for a response, given the responses recorded before it (oldest first). On a path with an id, the
    // entity with that id as the latest earlier successful response showed it (an object with that "id", at the top level
    // or in a top-level list), from any endpoint: the booking as the list or the check-in showed it before this checkout.
    // On a path without an id, the previous response on the same path. Before, prev was the previous response matching
    // the rule's pattern, which for /bookings/{id}/checkout could be another booking's checkout: rules written as
    // "prev.status == 'CheckedIn'" then failed on correct behaviour, and the failure replayed faithfully.
    public static JsonNode? Prior(IReadOnlyList<NetworkEvent> earlier, Invariant r, NetworkEvent e)
    {
        var path = e.Path.Split('?')[0];
        var ids = Probes.Ids(path);
        if (ids.Count == 0) return earlier.LastOrDefault(x => x.Path.Split('?')[0] == path && x.Method == e.Method && Applies(r, x))?.Json();
        var id = ids[^1];
        for (var i = earlier.Count - 1; i >= 0; i--)
        {
            var x = earlier[i];
            if (x.Status is < 200 or >= 300 || ReferenceEquals(x, e)) continue;
            if (Entity(x.Json(), id) is { } found) return found;
        }
        return null;
    }

    static JsonNode? Entity(JsonNode? j, string id)
    {
        static bool Is(JsonNode? n, string id) => n is JsonObject o && o["id"] is JsonValue v && v.ToString() == id;
        if (Is(j, id)) return j;
        IEnumerable<JsonNode?> lists = j switch { JsonArray a => [a], JsonObject o => o.Select(kv => kv.Value).Where(v => v is JsonArray), _ => [] };
        foreach (var list in lists.OfType<JsonArray>())
            if (list.FirstOrDefault(n => Is(n, id)) is { } item) return item;
        return null;
    }

    // true: held, false: violated, null: not applicable to this response (a field it needs is missing).
    public static Outcome Evaluate(Invariant r, NetworkEvent e, NetworkEvent? prev) => EvaluateWith(r, e, prev?.Json());

    public static Outcome EvaluateWith(Invariant r, NetworkEvent e, JsonNode? prev)
    {
        var trace = new Dictionary<string, string>();
        var scope = new Scope
        {
            Response = e.Json(), Request = e.RequestJson(), Prev = prev, Status = e.Status, Query = QueryOf(e.Url), Trace = trace
        };
        try
        {
            var v = Parsed.GetOrAdd(r.Check, Parser.Parse).Eval(scope);
            return new(v is bool b ? b : null, trace);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or ArithmeticException or OverflowException) { return new(null, trace); }
    }

    static JsonObject QueryOf(string url)
    {
        var o = new JsonObject();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return o;
        foreach (var part in u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            o[Uri.UnescapeDataString(kv[0])] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "";
        }
        return o;
    }

    static Func<NetworkEvent, bool>? Matcher(string? on)
    {
        var s = (on ?? "*").Trim();
        if (s is "" or "*" or "ANY") return _ => true;
        var m = OnPattern().Match(s);
        if (!m.Success) return null;
        var method = m.Groups[1].Success ? m.Groups[1].Value.ToUpperInvariant() : null;
        var path = m.Groups[2].Value;
        if (Uri.TryCreate(path, UriKind.Absolute, out var abs) && abs.Scheme.StartsWith("http")) path = Uri.UnescapeDataString(abs.AbsolutePath);
        path = path.Split('?')[0].TrimEnd('/');
        if (!path.StartsWith('/')) path = "/" + path;
        var rx = new Regex("(?:^|/)" + string.Concat(path.TrimStart('/').Split('/').Select((seg, i) => (i > 0 ? "/" : "") + Segment(seg))) + "/?$", RegexOptions.IgnoreCase);
        return e => (method is null || method is "*" or "ANY" || e.Method == method) && rx.IsMatch(e.Path);
    }

    static string Segment(string seg) => seg == "*" ? "[^/]+" : Regex.Replace(Regex.Escape(seg), @"\\\{[^}]*}|\\\*", m => m.Value == "\\*" ? "[^/]*" : "[^/]+");

    [GeneratedRegex(@"^(?:(GET|POST|PUT|PATCH|DELETE|ANY|\*)\s+)?(\S+)$", RegexOptions.IgnoreCase)] private static partial Regex OnPattern();

    // ---- expression language ----

    sealed class Missing { public static readonly Missing It = new(); }

    sealed class Scope
    {
        public JsonNode? Response, Request, Prev, Item;
        public JsonObject Query = new();
        public int Status;
        public Scope? Outer;
        public required Dictionary<string, string> Trace;
        public Scope With(JsonNode? item) => new() { Response = Response, Request = Request, Prev = Prev, Query = Query, Status = Status, Item = item, Outer = this, Trace = Trace };
    }

    abstract class Expr
    {
        public abstract object? Eval(Scope s);
        public virtual IEnumerable<string> Names() => [];
        // The same check as JavaScript for a Playwright spec (body, req, res, q in scope); null when it cannot be expressed.
        public abstract string? Js(bool item);
    }

    sealed class Lit(object? v) : Expr
    {
        public override object? Eval(Scope s) => v;
        public override string? Js(bool item) => v switch
        {
            null => "null", bool b => b ? "true" : "false", decimal d => d.ToString(CultureInfo.InvariantCulture),
            string t => System.Text.Json.JsonSerializer.Serialize(t), _ => null
        };
    }

    sealed class Name(string name) : Expr
    {
        public string Id => name;
        public override IEnumerable<string> Names() => [name];
        public override object? Eval(Scope s)
        {
            for (var sc = s; sc is not null; sc = sc.Outer)
                if (sc.Item is JsonObject o && o.TryGetPropertyValue(name, out var v)) return Value(v);
            object? r = name switch
            {
                "response" => Node(s.Response), "request" => Node(s.Request), "prev" => Node(s.Prev), "query" => s.Query,
                "status" => (decimal)s.Status, "it" or "item" => s.Item is null ? Missing.It : Value(s.Item),
                _ => s.Response is JsonObject root && root.TryGetPropertyValue(name, out var v) ? Value(v) : Missing.It
            };
            return r;
        }
        public override string? Js(bool item) => name switch
        {
            "response" => "body", "request" => "req", "status" => "res.status()", "query" => "q", "it" or "item" => item ? "it" : null, "prev" => null,
            _ when !JsName().IsMatch(name) => null,
            _ => item ? $"(it?.{name} ?? body?.{name})" : $"body?.{name}"
        };
    }

    sealed class Member(Expr target, string field, string text) : Expr
    {
        public override IEnumerable<string> Names() => target.Names();
        public override object? Eval(Scope s)
        {
            var t = target.Eval(s);
            var v = t is JsonObject o ? o.TryGetPropertyValue(field, out var n) ? Value(n) : Missing.It
                : t is JsonArray a && field is "length" or "count" ? (decimal)a.Count : Missing.It;
            Record(s, text, v);
            return v;
        }
        public override string? Js(bool item) => target.Js(item) is { } t && JsName().IsMatch(field) ? $"{t}?.{field}" : null;
    }

    sealed class Index(Expr target, Expr index, string text) : Expr
    {
        public override IEnumerable<string> Names() => target.Names().Concat(index.Names());
        public override object? Eval(Scope s)
        {
            var t = target.Eval(s); var i = index.Eval(s);
            object? v = t is JsonArray a && i is decimal d && d >= 0 && d < a.Count ? Value(a[(int)d]) : t is JsonObject o && i is string k && o.TryGetPropertyValue(k, out var n) ? Value(n) : Missing.It;
            Record(s, text, v);
            return v;
        }
        public override string? Js(bool item) => target.Js(item) is { } t && index.Js(item) is { } i ? $"{t}?.[{i}]" : null;
    }

    sealed class Traced(Name inner) : Expr
    {
        public override IEnumerable<string> Names() => inner.Names();
        public override object? Eval(Scope s) { var v = inner.Eval(s); if (s.Item is null) Record(s, inner.Id, v); return v; }
        public override string? Js(bool item) => inner.Js(item);
    }

    static void Record(Scope s, string text, object? v)
    {
        if (s.Item is not null || s.Trace.Count >= 12 || s.Trace.ContainsKey(text) || v is JsonObject or JsonArray) return;
        s.Trace[text] = Show(v);
    }

    static string Show(object? v) => v switch
    {
        Missing => "(missing)", null => "null", string t => $"\"{t}\"", bool b => b ? "true" : "false",
        decimal d => d.ToString(CultureInfo.InvariantCulture), DateTime dt => dt.ToString("yyyy-MM-dd"), _ => v.ToString() ?? ""
    };

    sealed class Unary(string op, Expr x) : Expr
    {
        public override IEnumerable<string> Names() => x.Names();
        public override object? Eval(Scope s)
        {
            var v = x.Eval(s);
            return op == "!" ? v is bool b ? !b : Missing.It : v is decimal d ? -d : Missing.It;
        }
        public override string? Js(bool item) => x.Js(item) is { } j ? $"{op}({j})" : null;
    }

    sealed class Binary(string op, Expr l, Expr r) : Expr
    {
        public override IEnumerable<string> Names() => l.Names().Concat(r.Names());
        public override object? Eval(Scope s)
        {
            if (op is "&&" or "||")
            {
                var a = l.Eval(s);
                if (op == "&&" && a is false || op == "||" && a is true) return a;
                var b = r.Eval(s);
                if (op == "&&" && b is false || op == "||" && b is true) return b;
                return a is bool && b is bool ? b : Missing.It;
            }
            var x = l.Eval(s); var y = r.Eval(s);
            if (x is Missing || y is Missing) return Missing.It;
            switch (op)
            {
                case "==": return Eq(x, y);
                case "!=": return !Eq(x, y);
            }
            if (op is "<" or "<=" or ">" or ">=")
            {
                int c;
                if (Num(x) is decimal a && Num(y) is decimal b) c = a.CompareTo(b);
                else if (x is DateTime da && y is DateTime db) c = da.CompareTo(db);
                else if (x is string sa && y is string sb) c = string.CompareOrdinal(sa, sb);
                else return Missing.It;
                return op switch { "<" => c < 0, "<=" => c <= 0, ">" => c > 0, _ => c >= 0 };
            }
            if (op == "+" && (x is string || y is string)) return $"{Plain(x)}{Plain(y)}";
            if (Num(x) is not decimal p || Num(y) is not decimal q) return Missing.It;
            return op switch { "+" => p + q, "-" => p - q, "*" => p * q, "/" => q == 0 ? Missing.It : p / q, "%" => q == 0 ? Missing.It : p % q, _ => Missing.It };
        }

        public override string? Js(bool item)
        {
            if (l.Js(item) is not { } a || r.Js(item) is not { } b) return null;
            return op switch { "==" => $"eq({a}, {b})", "!=" => $"!eq({a}, {b})", _ => $"({a} {op} {b})" };
        }

        static bool Eq(object? x, object? y)
        {
            if (Num(x) is { } a && Num(y) is { } b) return a == b;
            if (x is string sa && y is string sb) return sa == sb;
            return Equals(x, y);
        }
    }

    sealed class Call(string fn, List<Expr> args) : Expr
    {
        public override IEnumerable<string> Names() => args.SelectMany(a => a.Names());
        public override object? Eval(Scope s)
        {
            switch (fn)
            {
                case "sum" or "count" or "all" or "any" or "min" or "max":
                {
                    if (Arg(0, s) is not JsonArray list) return Missing.It;
                    var vals = new List<object?>();
                    foreach (var item in list)
                        vals.Add(args.Count > 1 ? args[1].Eval(s.With(item)) : Value(item));
                    switch (fn)
                    {
                        case "count": return args.Count > 1 ? vals.Count(v => v is true) : list.Count;
                        case "all": return vals.Any(v => v is false) ? false : vals.All(v => v is bool) ? true : Missing.It;
                        case "any": return vals.Any(v => v is true) ? true : vals.All(v => v is bool) ? false : Missing.It;
                    }
                    if (vals.Any(v => Num(v) is null)) return Missing.It;
                    var nums = vals.Select(v => Num(v)!.Value).ToList();
                    return fn switch { "sum" => nums.Sum(), "min" => nums.Count == 0 ? Missing.It : nums.Min(), _ => nums.Count == 0 ? Missing.It : nums.Max() };
                }
                case "exists": return Arg(0, s) is not (Missing or null);
                case "abs": return Num(Arg(0, s)) is decimal a ? Math.Abs(a) : Missing.It;
                case "round": return Num(Arg(0, s)) is decimal x ? Math.Round(x, args.Count > 1 && Num(Arg(1, s)) is decimal d ? (int)d : 0, MidpointRounding.AwayFromZero) : Missing.It;
                case "len": return Arg(0, s) switch { string t => (decimal)t.Length, JsonArray arr => (decimal)arr.Count, JsonObject o => (decimal)o.Count, _ => Missing.It };
                case "lower": return Arg(0, s) is string l ? l.ToLowerInvariant() : Missing.It;
                case "upper": return Arg(0, s) is string u ? u.ToUpperInvariant() : Missing.It;
                case "number": return Num(Arg(0, s)) is decimal n ? n : Missing.It;
                case "contains":
                {
                    var hay = Arg(0, s); var needle = Arg(1, s);
                    if (hay is Missing || needle is Missing) return Missing.It;
                    return hay is JsonArray arr ? arr.Any(i => Equals(Value(i), needle) || Num(Value(i)) is decimal p && Num(needle) is decimal q && p == q)
                        : hay is string t && needle is not null ? t.Contains(Plain(needle), StringComparison.Ordinal) : Missing.It;
                }
                case "today": return DateTime.UtcNow.Date;
                case "date": return Arg(0, s) is string ds && Date(ds) is DateTime dt ? dt : Missing.It;
                default: throw new FormatException($"unknown function {fn}()");
            }
        }

        object? Arg(int i, Scope s) => i < args.Count ? args[i].Eval(s) : Missing.It;

        public override string? Js(bool item)
        {
            var a = args.Select((x, i) => x.Js(item || (i == 1 && fn is "sum" or "count" or "all" or "any" or "min" or "max"))).ToList();
            if (a.Any(x => x is null)) return null;
            string? A(int i) => i < a.Count ? a[i] : null;
            return fn switch
            {
                "sum" when a.Count > 1 => $"({A(0)} ?? []).reduce((s, it) => s + ({A(1)}), 0)",
                "sum" => $"({A(0)} ?? []).reduce((s, x) => s + x, 0)",
                "count" when a.Count > 1 => $"({A(0)} ?? []).filter((it) => {A(1)}).length",
                "count" => $"({A(0)} ?? []).length",
                "all" => $"({A(0)} ?? []).every((it) => {A(1)})",
                "any" => $"({A(0)} ?? []).some((it) => {A(1)})",
                "min" or "max" => $"Math.{fn}(...({A(0)} ?? []).map((it) => {A(1) ?? "it"}))",
                "exists" => $"({A(0)} !== undefined && {A(0)} !== null)",
                "abs" => $"Math.abs({A(0)})",
                "round" => $"(Math.round(({A(0)}) * 10 ** {A(1) ?? "0"}) / 10 ** {A(1) ?? "0"})",
                "len" => $"{A(0)}?.length",
                "lower" => $"String({A(0)}).toLowerCase()",
                "upper" => $"String({A(0)}).toUpperCase()",
                "number" => $"Number({A(0)})",
                "contains" => $"({A(0)} ?? '').includes({A(1)})",
                _ => null
            };
        }
    }

    public static string? ToJs(string check)
    {
        try { return Parsed.GetOrAdd(check, Parser.Parse).Js(false); }
        catch (FormatException) { return null; }
    }

    [GeneratedRegex(@"^[A-Za-z_$][\w$]*$")] private static partial Regex JsName();

    static readonly string[] Functions = ["sum", "count", "all", "any", "min", "max", "exists", "abs", "round", "len", "lower", "upper", "number", "contains", "today", "date"];

    // "MM/YY" and "MM/YYYY" card expiries mean the end of that month; ISO dates as written.
    static DateTime? Date(string s)
    {
        var m = Regex.Match(s.Trim(), @"^(\d{1,2})\s*/\s*(\d{2}|\d{4})$");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var mo) && mo is >= 1 and <= 12)
        {
            var y = int.Parse(m.Groups[2].Value); if (y < 100) y += 2000;
            return new DateTime(y, mo, DateTime.DaysInMonth(y, mo));
        }
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d.Date : null;
    }

    public static decimal? Number(JsonNode? n) => Value(n) as decimal?;
    static object? Node(JsonNode? n) => n is null ? Missing.It : Value(n);
    static object? Value(JsonNode? n)
    {
        if (n is not JsonValue v) return n;
        return v.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.Number => decimal.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : (object?)v.ToJsonString(),
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            System.Text.Json.JsonValueKind.String => v.GetValue<string>(),
            System.Text.Json.JsonValueKind.Null => null,
            _ => v
        };
    }
    static decimal? Num(object? v) => v switch { decimal d => d, string t when decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) => d, _ => null };
    static string Plain(object? v) => v switch { decimal d => d.ToString(CultureInfo.InvariantCulture), null => "", _ => v.ToString() ?? "" };

    sealed class Parser
    {
        readonly List<(string Kind, string Text)> _t; int _i;
        readonly HashSet<string> _params = []; // arrow-function parameters ("item => item.qty"), which mean the list item
        Parser(List<(string, string)> t) => _t = t;

        public static Expr Parse(string text)
        {
            var p = new Parser(Lex(text));
            var e = p.Or();
            if (p._i < p._t.Count) throw new FormatException($"unexpected '{p._t[p._i].Text}'");
            return e;
        }

        static List<(string, string)> Lex(string s)
        {
            var list = new List<(string, string)>();
            for (var i = 0; i < s.Length;)
            {
                var c = s[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (char.IsDigit(c) || c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1]))
                {
                    var j = i; while (j < s.Length && (char.IsDigit(s[j]) || s[j] == '.')) j++;
                    list.Add(("num", s[i..j])); i = j; continue;
                }
                if (char.IsLetter(c) || c is '_' or '$')
                {
                    var j = i; while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] is '_' or '$')) j++;
                    var w = s[i..j];
                    list.Add(w.ToLowerInvariant() switch { "and" => ("op", "&&"), "or" => ("op", "||"), "not" => ("op", "!"), _ => ("id", w) });
                    i = j; continue;
                }
                if (c is '"' or '\'')
                {
                    var sb = new StringBuilder(); var j = i + 1;
                    for (; j < s.Length && s[j] != c; j++) { if (s[j] == '\\' && j + 1 < s.Length) j++; sb.Append(s[j]); }
                    if (j >= s.Length) throw new FormatException("unterminated string");
                    list.Add(("str", sb.ToString())); i = j + 1; continue;
                }
                var two = i + 1 < s.Length ? s.Substring(i, 2) : "";
                if (two is "=>" or "==" or "!=" or "<=" or ">=" or "&&" or "||") { list.Add(("op", two)); i += 2; continue; }
                if ("+-*/%<>!=().,[]".Contains(c)) { list.Add(("op", c == '=' ? "==" : c.ToString())); i++; continue; }
                throw new FormatException($"unexpected character '{c}'");
            }
            return list;
        }

        // Smaller models write list conditions as JavaScript arrows: "x => x.qty > 0" or "(x) => x.qty > 0".
        Expr Arg()
        {
            string? p = null;
            if (_i + 1 < _t.Count && _t[_i].Kind == "id" && _t[_i + 1] == ("op", "=>")) { p = _t[_i].Text; _i += 2; }
            else if (_i + 3 < _t.Count && _t[_i] == ("op", "(") && _t[_i + 1].Kind == "id" && _t[_i + 2] == ("op", ")") && _t[_i + 3] == ("op", "=>")) { p = _t[_i + 1].Text; _i += 4; }
            if (p is null) return Or();
            _params.Add(p);
            try { return Or(); } finally { _params.Remove(p); }
        }

        bool Is(string op) => _i < _t.Count && _t[_i].Kind == "op" && _t[_i].Text == op;
        void Expect(string op) { if (!Is(op)) throw new FormatException(_i < _t.Count ? $"expected '{op}' before '{_t[_i].Text}'" : $"expected '{op}' at the end"); _i++; }

        Expr Or() { var e = And(); while (Is("||")) { _i++; e = new Binary("||", e, And()); } return e; }
        Expr And() { var e = Not(); while (Is("&&")) { _i++; e = new Binary("&&", e, Not()); } return e; }
        Expr Not() { if (Is("!")) { _i++; return new Unary("!", Not()); } return Cmp(); }
        Expr Cmp()
        {
            var e = Add();
            foreach (var op in new[] { "==", "!=", "<=", ">=", "<", ">" })
                if (Is(op)) { _i++; return new Binary(op, e, Add()); }
            return e;
        }
        Expr Add() { var e = Mul(); while (Is("+") || Is("-")) { var op = _t[_i++].Text; e = new Binary(op, e, Mul()); } return e; }
        Expr Mul() { var e = Neg(); while (Is("*") || Is("/") || Is("%")) { var op = _t[_i++].Text; e = new Binary(op, e, Neg()); } return e; }
        Expr Neg() { if (Is("-")) { _i++; return new Unary("-", Neg()); } return Postfix(); }

        Expr Postfix()
        {
            var e = Primary(out var text);
            while (true)
            {
                if (Is(".")) { _i++; var f = Ident(); text += "." + f; e = new Member(e, f, text); }
                else if (Is("[")) { _i++; var start = _i; var idx = Or(); Expect("]"); text += "[" + string.Concat(_t.Skip(start).Take(_i - start - 1).Select(t => t.Text)) + "]"; e = new Index(e, idx, text); }
                else return e;
            }
        }

        string Ident() => _i < _t.Count && _t[_i].Kind == "id" ? _t[_i++].Text : throw new FormatException("expected a field name");

        Expr Primary(out string text)
        {
            if (_i >= _t.Count) throw new FormatException("the expression ends early");
            var (kind, t) = _t[_i++]; text = t;
            switch (kind)
            {
                case "num": return decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? new Lit(d) : throw new FormatException($"bad number {t}");
                case "str": return new Lit(t);
                case "id":
                    switch (t.ToLowerInvariant()) { case "true": return new Lit(true); case "false": return new Lit(false); case "null": return new Lit(null); }
                    if (Is("("))
                    {
                        var fn = t.ToLowerInvariant();
                        if (!Functions.Contains(fn)) throw new FormatException($"unknown function {t}()");
                        _i++; var args = new List<Expr>();
                        if (!Is(")")) { args.Add(Arg()); while (Is(",")) { _i++; args.Add(Arg()); } }
                        Expect(")");
                        return new Call(fn, args);
                    }
                    return new Traced(new Name(_params.Contains(t) ? "it" : t));
                case "op" when t == "(":
                    var e = Or(); Expect(")"); return e;
            }
            throw new FormatException($"unexpected '{t}'");
        }
    }
}
