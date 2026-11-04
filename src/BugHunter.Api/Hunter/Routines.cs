using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BugHunter.Api.Hunter;

// Probe routines: tested multi-step probes the engine runs deterministically. The model only chooses a routine and its
// arguments (elements named by their visible label or name), so it plans instead of spending its budget on single clicks,
// and every routine runs the same way each time.
public interface IRoutine
{
    string Name { get; }
    string Usage { get; } // one line for the model's prompt
    Task RunAsync(RoutineContext r, IReadOnlyDictionary<string, string> args, CancellationToken ct);
}

public class RoutineContext
{
    // Executes one action on the current page, records it as a step, observes the result and returns the new page.
    public required Func<HuntAction, ElementInfo?, CancellationToken, Task<(bool Ok, string? Error, PageSnapshot Page)>> Do { get; init; }
    public required Action<Oracles.Hit> Report { get; init; }
    public required Func<int> StepsLeft { get; init; }
    public required PageSnapshot Page { get; set; }
    public List<string> Problems { get; } = [];

    static readonly string[] Fields = ["textbox", "searchbox", "spinbutton", "combobox"];
    static readonly string[] Pressable = ["button", "link"];

    // Exact match on label or accessible name first, then a case-insensitive partial match.
    public ElementInfo? Find(string? text, params string[] roles)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        var pool = Page.Elements.Where(e => e.Disabled != true && (roles.Length == 0 || roles.Contains(e.Role))).ToList();
        bool Is(string? s) => string.Equals(s?.Trim(), t, StringComparison.OrdinalIgnoreCase);
        bool Has(string? s) => s?.Contains(t, StringComparison.OrdinalIgnoreCase) == true;
        return pool.FirstOrDefault(e => Is(e.Label) || Is(e.Name)) ?? pool.FirstOrDefault(e => Has(e.Label) || Has(e.Name) || Has(e.Placeholder));
    }

    public async Task<PageSnapshot?> Act(string type, ElementInfo? el, string? value, CancellationToken ct, string? url = null)
    {
        if (StepsLeft() <= 0) return null;
        var (ok, error, page) = await Do(new HuntAction { Type = type, Ref = el?.Ref, Value = value, Url = url }, el, ct);
        Page = page;
        if (!ok) Problems.Add(error ?? $"{type} failed");
        return ok ? page : null;
    }

    public async Task<PageSnapshot?> Fill(string? field, string value, CancellationToken ct)
    {
        var el = Find(field, Fields);
        if (el is null) { Problems.Add($"No field named '{field}' on this page."); return null; }
        return await Act(el.Role == "combobox" ? "select" : "fill", el, value, ct);
    }

    public async Task<PageSnapshot?> Press(string? name, CancellationToken ct)
    {
        var el = Find(name, Pressable);
        if (el is null) { Problems.Add($"No button or link named '{name}' on this page."); return null; }
        return await Act("click", el, null, ct);
    }
}

public static partial class Routines
{
    public static readonly IRoutine[] All = [new FillForm(), new BoundaryValues(), new RepeatSubmit(), new CaseVariants(), new BackAndResubmit(), new Visit()];
    public static IRoutine? Get(string? name) => All.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

    static string? Arg(IReadOnlyDictionary<string, string> a, string key) => a.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    class FillForm : IRoutine
    {
        public string Name => "fill-form";
        public string Usage => "fill-form {submit: button name, \"<field label>\": value, ...}: fills each field by its label, then presses submit.";
        public async Task RunAsync(RoutineContext r, IReadOnlyDictionary<string, string> a, CancellationToken ct)
        {
            foreach (var (field, value) in a.Where(kv => kv.Key != "submit"))
                await r.Fill(field, value, ct);
            if (Arg(a, "submit") is { } submit) await r.Press(submit, ct);
        }
    }

    class BoundaryValues : IRoutine
    {
        public string Name => "boundary-values";
        public string Usage => "boundary-values {field, submit, values?}: submits the field with -1, 0, 1, just above the stated stock or max, 99999 and empty (or your comma-separated values).";
        public async Task RunAsync(RoutineContext r, IReadOnlyDictionary<string, string> a, CancellationToken ct)
        {
            var field = Arg(a, "field"); var submit = Arg(a, "submit");
            var values = Arg(a, "values")?.Split(',').Select(v => v.Trim()).ToList() ?? Defaults(r, field);
            foreach (var v in values)
            {
                if (await r.Fill(field, v, ct) is null) return;
                if (submit is not null && await r.Press(submit, ct) is null) return;
            }
        }

        static List<string> Defaults(RoutineContext r, string? field)
        {
            var list = new List<string> { "-1", "0", "1" };
            var max = r.Find(field)?.Max is { } m && int.TryParse(m, out var mx) ? mx
                : Stock().Match(r.Page.Text) is { Success: true } s ? int.Parse(s.Groups[1].Success ? s.Groups[1].Value : s.Groups[2].Value) : (int?)null;
            if (max is int limit) list.Add((limit + 1).ToString());
            list.Add("99999"); list.Add("");
            return list;
        }
    }

    class RepeatSubmit : IRoutine
    {
        public string Name => "repeat-submit";
        public string Usage => "repeat-submit {submit, field?, value?, times?}: makes the same submission 2+ times (same coupon, same order, same form).";
        public async Task RunAsync(RoutineContext r, IReadOnlyDictionary<string, string> a, CancellationToken ct)
        {
            var times = int.TryParse(Arg(a, "times"), out var t) ? Math.Clamp(t, 2, 5) : 2;
            var created = new List<(string Path, string Id)>();
            for (var i = 0; i < times; i++)
            {
                if (Arg(a, "field") is { } field && await r.Fill(field, Arg(a, "value") ?? "", ct) is null) return;
                if (await r.Press(Arg(a, "submit"), ct) is not { } page) return;
                foreach (var n in page.Network.Where(n => n.Status == 201 && n.Method == "POST"))
                    if (n.Json() is JsonObject o && o["id"] is JsonValue id) created.Add((n.Path, id.ToString()));
            }
            // The same submission creating a new record each time is a duplicate unless the app means to allow it.
            var dup = created.GroupBy(c => c.Path).FirstOrDefault(g => g.Select(c => c.Id).Distinct().Count() > 1);
            if (dup is not null)
                r.Report(new("duplicate-submission:" + dup.Key, "duplicate-submission", "Repeated submission created duplicate records", "Medium",
                    "Submitting the same thing again is rejected, or returns the existing record.",
                    $"POST {dup.Key} returned 201 and created {string.Join(", ", dup.Select(c => c.Id))}.", Method: "POST", Path: dup.Key));
        }
    }

    class CaseVariants : IRoutine
    {
        public string Name => "case-variants";
        public string Usage => "case-variants {field, submit, term}: searches the term in lower, UPPER and Title case and compares the results.";
        public async Task RunAsync(RoutineContext r, IReadOnlyDictionary<string, string> a, CancellationToken ct)
        {
            var term = Arg(a, "term") ?? "";
            if (term.Length == 0) { r.Problems.Add("case-variants needs a term."); return; }
            var counts = new Dictionary<string, int>();
            foreach (var v in new[] { term.ToLowerInvariant(), term.ToUpperInvariant(), char.ToUpperInvariant(term[0]) + term[1..].ToLowerInvariant() }.Distinct())
            {
                if (await r.Fill(Arg(a, "field"), v, ct) is null) return;
                if (await r.Press(Arg(a, "submit"), ct) is not { } page) return;
                // Count results from the search response the page made, falling back to nothing when it made none.
                var res = page.Network.LastOrDefault(n => n.Method == "GET" && n.Status is >= 200 and < 300 && n.Url.Contains(Uri.EscapeDataString(v), StringComparison.Ordinal));
                if (res?.Json() is { } body && (body is JsonArray ? body : body["items"] ?? body["results"] ?? body["products"]) is JsonArray items) counts[v] = items.Count;
            }
            if (counts.Count >= 2 && counts.Values.Distinct().Count() > 1)
                r.Report(new("search-case-sensitive", "case-variants", "Search results depend on letter case", "High",
                    $"Searching \"{term}\" returns the same results in any letter case.",
                    string.Join(", ", counts.Select(c => $"\"{c.Key}\" → {c.Value} results")) + "."));
        }
    }

    class BackAndResubmit : IRoutine
    {
        public string Name => "back-and-resubmit";
        public string Usage => "back-and-resubmit {submit}: submits, goes back with the browser, and submits again.";
        public async Task RunAsync(RoutineContext r, IReadOnlyDictionary<string, string> a, CancellationToken ct)
        {
            var submit = Arg(a, "submit");
            if (await r.Press(submit, ct) is null) return;
            if (await r.Act("back", null, null, ct) is null) return;
            await r.Press(submit, ct);
        }
    }

    class Visit : IRoutine
    {
        public string Name => "visit";
        public string Usage => "visit {link | url}: opens a link by its visible text, or a URL on the same site.";
        public async Task RunAsync(RoutineContext r, IReadOnlyDictionary<string, string> a, CancellationToken ct)
        {
            if (Arg(a, "link") is { } link && r.Find(link, "link", "button") is { } el) await r.Act("click", el, null, ct);
            else if (Arg(a, "url") is { } url) await r.Act("navigate", null, null, ct, url);
            else r.Problems.Add($"No link named '{Arg(a, "link")}' on this page.");
        }
    }

    [GeneratedRegex(@"[Oo]nly (\d+) left|(\d+) in stock")] private static partial Regex Stock();
}
