using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BugHunter.Api.Common;
using BugHunter.Api.Contracts;
using BugHunter.Api.Hunter;
using BugHunter.Api.Models;
using BugHunter.Api.Projects;

namespace BugHunter.Api.Twin;

// AI understanding: a model reads the twin's observed and declared layers, the stories, documents and skill packs, and
// proposes entities, features, lifecycles and rules. A deterministic verifier then checks every proposal against the
// twin: an item survives only with the parts it can tie to a recorded or declared endpoint, field, page, story or role.
// Admitted items are "inferred" (or "verified" when everything they rest on was observed); refused ones are kept, with
// the reason, so the verifier's work is visible. Nothing here produces a finding.
public static partial class Understanding
{
    public static readonly JsonObject Schema = Obj(new()
    {
        ["entities"] = Arr(Obj(new() { ["name"] = Str(), ["fields"] = Arr(Str()), ["endpoints"] = Arr(Str()) })),
        ["features"] = Arr(Obj(new() { ["name"] = Str(), ["pages"] = Arr(Str()), ["endpoints"] = Arr(Str()), ["stories"] = Arr(Str()), ["roles"] = Arr(Str()) })),
        ["lifecycles"] = Arr(Obj(new()
        {
            ["entity"] = Str(), ["field"] = Str(), ["states"] = Arr(Str()),
            ["transitions"] = Arr(Obj(new() { ["from"] = Str(), ["to"] = Str(), ["endpoint"] = Str(), ["role"] = Str() }))
        })),
        ["rules"] = Arr(Obj(new() { ["statement"] = Str(), ["story"] = Str(), ["on"] = Str(), ["check"] = Str() })),
    });

    static JsonObject Obj(JsonObject props) => new() { ["type"] = "object", ["properties"] = props };
    static JsonObject Arr(JsonObject items) => new() { ["type"] = "array", ["items"] = items };
    static JsonObject Str() => new() { ["type"] = "string" };

    public const string System = """
You are a senior QA analyst building a model of a web application from evidence. You get the pages and API the test
engine recorded or the API specification declares (with the fields each endpoint sends and returns, and the HTTP
statuses seen per role), the user stories with acceptance criteria, and the product documents. Propose:
- entities: the business objects, with their fields as they appear in the API (use the exact field names listed),
- features: what users can do, with the pages and endpoints involved, the stories that describe them, the roles,
- lifecycles: entities whose status changes, with the states and the endpoint that makes each transition,
- rules: what must always hold, written as checks on one endpoint's response.
Use ONLY endpoints, field names, pages, story keys and roles that appear below; anything else is discarded.
Rule checks use this language:
""" + InvariantRules.Language + """
Reply with one JSON object: {"entities":[...], "features":[...], "lifecycles":[...], "rules":[...]}. For example (the
shape only; use this app's real endpoints and fields):
{"entities":[{"name":"Order","fields":["id","status","total"],"endpoints":["GET /api/orders"]}],
 "features":[{"name":"Place an order","pages":["#/checkout"],"endpoints":["POST /api/checkout"],"stories":["ABC-12"],"roles":["shopper"]}],
 "lifecycles":[{"entity":"Order","field":"status","states":["Placed","Shipped"],"transitions":[{"from":"Placed","to":"Shipped","endpoint":"POST /api/orders/{id}/ship","role":"staff"}]}],
 "rules":[{"statement":"The total is the sum of the lines","story":"ABC-12","on":"GET /api/orders/{id}","check":"total == sum(lines, price * qty)"}]}
""";

    public static string Prompt(Project p, AppTwin t, KnowledgeSnapshot? k, IReadOnlyList<SkillPack> packs, bool local)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"APP: {p.Name}. {p.Mission}");
        sb.AppendLine($"ROLES: {string.Join(", ", p.Roles.Select(r => $"{r.Id} ({r.Name})"))}");
        sb.AppendLine("PAGES: " + string.Join(", ", t.States.Select(s => s.Id).Take(local ? 20 : 40)));
        sb.AppendLine("API (method path [statuses by role] request fields | response fields):");
        foreach (var e in t.Endpoints.Take(local ? 30 : 60))
        {
            var statuses = string.Join("; ", e.StatusesByRole.Select(kv => $"{kv.Key} {string.Join("/", kv.Value)}"));
            var req = e.RequestFields.Union(e.DeclaredRequestFields).Take(local ? 10 : 20);
            var res = e.ResponseFields.Union(e.DeclaredResponseFields).Take(local ? 14 : 30);
            sb.AppendLine($"- {e.Key} [{(statuses.Length > 0 ? statuses : "declared only")}] request: {string.Join(", ", req)} | response: {string.Join(", ", res)}");
        }
        if (k is not null)
        {
            sb.AppendLine("STORIES:");
            foreach (var s in k.Stories.Take(12)) sb.AppendLine($"- {s.Key} {s.Title}: {Regex.Replace(s.Criteria, @"\s*\n\s*", " / ")}");
            var docs = KnowledgeDiffer.Grounding(k, local ? 2500 : 6000);
            if (docs.Length > 0) sb.AppendLine("DOCUMENTS:\n" + docs);
        }
        foreach (var pack in packs.Take(2)) sb.AppendLine($"SKILLS: {pack.Title}\n{(pack.Body.Length > 1200 ? pack.Body[..1200] : pack.Body)}");
        if (t.Entities.Count + t.Features.Count > 0)
            sb.AppendLine($"ALREADY KNOWN (add what is missing, correct what is wrong): entities {string.Join(", ", t.Entities.Select(x => x.Name))}; features {string.Join(", ", t.Features.Select(x => x.Name))}.");
        return sb.ToString();
    }

    public static string InputHash(string prompt) => KnowledgeDiffer.Hash(prompt);

    public record Result(AppTwin Twin, int Proposed, int Admitted, string? Error);

    // One model call, then the verifier. Returns the next twin version, or the error and no version.
    public static async Task<Result> RunAsync(ModelGateway gateway, ResolvedModel m, Project p, AppTwin prev, KnowledgeSnapshot? k, IReadOnlyList<SkillPack> packs, CancellationToken ct)
    {
        var local = m.Provider is "Ollama" or "Local";
        var prompt = Prompt(p, prev, k, packs, local);
        var sw = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(local ? TimeSpan.FromMinutes(12) : TimeSpan.FromMinutes(3));
        ChatResponse res;
        try { res = await gateway.CompleteAsync(m, new ChatRequest(System, prompt, 0.2, local ? 2500 : 4000, [], "bug-hunter-understand", null, Schema), deadline.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(prev, 0, 0, "The model did not answer in time."); }
        var parsed = OutputContracts.Parse(res.Text);
        if (parsed.Value is not JsonObject reply) return new(prev, 0, 0, parsed.Error ?? "The reply was not a JSON object.");

        var t = TwinBuilder.Copy(prev, p);
        var (pin, pout) = ModelGateway.PricePer1K(m.Model);
        t.Understanding = new TwinUnderstanding
        {
            Model = $"{m.Provider}:{m.Model}", Calls = 1, TokensIn = res.TokensIn, TokensOut = res.TokensOut, CostUsd = Math.Round(res.TokensIn / 1000.0 * pin + res.TokensOut / 1000.0 * pout, 5),
            Seconds = Math.Round(sw.Elapsed.TotalSeconds, 1), InputHash = InputHash(prompt)
        };
        var (proposed, admitted) = Verify(t, p, k, reply);
        t.Understanding.Proposed = proposed; t.Understanding.Admitted = admitted;
        return new(t, proposed, admitted, null);
    }

    // ---------------- the verifier ----------------

    static string Leaf(string f) => f.Split('.').Last().Replace("[]", "").ToLowerInvariant();

    static TwinEndpoint? Endpoint(AppTwin t, string? text)
    {
        var m = EndpointText().Match(text ?? "");
        if (!m.Success) return null;
        // Fold both {param} placeholders and concrete ids (/api/bookings/B-1/cancel) the way recorded endpoints are folded,
        // on the plain path (a URI would escape the braces).
        var key = $"{m.Groups[1].Value.ToUpperInvariant()} {Probes.FoldIds(Specs.Template(m.Groups[2].Value.Split('?')[0]))}";
        return t.Endpoints.FirstOrDefault(e => e.Key == key);
    }

    static HashSet<string> Leaves(TwinEndpoint e) =>
        e.RequestFields.Concat(e.ResponseFields).Concat(e.DeclaredRequestFields).Concat(e.DeclaredResponseFields).Select(Leaf).ToHashSet();

    static bool Succeeded(TwinEndpoint e) => e.StatusesByRole.Values.Any(c => c.Any(s => s is >= 200 and < 300));

    static string Page(string p) { var s = p.Replace(" [signed in]", "").TrimStart('/').Split('?')[0]; s = Regex.Replace(s, @"\{[^}]+\}", "{id}"); return s is "" or "#" ? "#/" : s; }

    static List<string> Strings(JsonNode? n) => (n as JsonArray ?? []).Select(x => J.Text(x).Trim()).Where(x => x.Length > 0).Distinct().ToList();

    public static (int Proposed, int Admitted) Verify(AppTwin t, Project p, KnowledgeSnapshot? k, JsonObject reply)
    {
        int proposed = 0, admitted = 0; var v = t.Version;
        t.Rejected = [];
        void Reject(string kind, string name, string reason) => t.Rejected.Add(new(kind, name, reason));
        var stories = k?.Stories.Select(s => s.Key).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var roles = p.Roles.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pages = t.States.Select(s => Page(s.Id)).ToHashSet();
        var evidence = new TwinEvidence("model", t.Understanding?.Model ?? "model", "proposed by a model, checked by the verifier");

        foreach (var o in (reply["entities"] as JsonArray ?? []).OfType<JsonObject>())
        {
            proposed++;
            var name = J.Text(o["name"]).Trim();
            if (name.Length == 0) { Reject("entity", "(unnamed)", "no name"); continue; }
            var eps = Strings(o["endpoints"]).Select(x => Endpoint(t, x)).OfType<TwinEndpoint>().Distinct().ToList();
            var pool = (eps.Count > 0 ? eps : t.Endpoints).SelectMany(Leaves).ToHashSet();
            var fields = Strings(o["fields"]).Where(f => pool.Contains(Leaf(f))).ToList();
            if (fields.Count == 0 || (eps.Count == 0 && fields.Count < 2)) { Reject("entity", name, "none of its fields appear in the API"); continue; }
            var e = t.Entities.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (e is null) { t.Entities.Add(e = new TwinEntity { Name = name, Status = "inferred" }); t.Changes.Understood.Add($"entity {name}"); }
            e.Fields = e.Fields.Union(fields).ToList(); e.Endpoints = e.Endpoints.Union(eps.Select(x => x.Key)).ToList();
            e.Saw(v, "model", evidence);
            e.Status = eps.Count > 0 && eps.All(x => x.Status != "declared") ? "verified" : e.Status == "verified" ? "verified" : "inferred";
            admitted++;
        }

        foreach (var o in (reply["features"] as JsonArray ?? []).OfType<JsonObject>())
        {
            proposed++;
            var name = J.Text(o["name"]).Trim();
            var eps = Strings(o["endpoints"]).Select(x => Endpoint(t, x)).OfType<TwinEndpoint>().Distinct().ToList();
            var pg = Strings(o["pages"]).Select(Page).Where(pages.Contains).ToList();
            if (name.Length == 0 || eps.Count + pg.Count == 0) { Reject("feature", name.Length > 0 ? name : "(unnamed)", "none of its pages or endpoints exist"); continue; }
            var f = t.Features.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (f is null) { t.Features.Add(f = new TwinFeature { Name = name, Status = "inferred" }); t.Changes.Understood.Add($"feature {name}"); }
            f.Endpoints = f.Endpoints.Union(eps.Select(x => x.Key)).ToList(); f.Pages = f.Pages.Union(pg).ToList();
            f.Stories = f.Stories.Union(Strings(o["stories"]).Where(stories.Contains)).ToList();
            f.ForRoles = f.ForRoles.Union(Strings(o["roles"]).Where(roles.Contains)).ToList();
            f.Saw(v, "model", evidence);
            f.Status = eps.Any(Succeeded) || pg.Count > 0 ? "verified" : "inferred";
            admitted++;
        }

        foreach (var o in (reply["lifecycles"] as JsonArray ?? []).OfType<JsonObject>())
        {
            proposed++;
            var entity = J.Text(o["entity"]).Trim(); var field = J.Text(o["field"]).Trim();
            var assumed = false;
            // A lifecycle without its field named most likely means the conventional "status"; say so if it is assumed.
            if (field.Length == 0 && t.Endpoints.SelectMany(Leaves).Contains("status")) { field = "status"; assumed = true; }
            var transitions = (o["transitions"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(x => (From: J.Text(x["from"]), To: J.Text(x["to"]), Ep: Endpoint(t, J.Text(x["endpoint"])), Role: J.Text(x["role"])))
                .Where(x => x.Ep is not null && x.To.Length > 0).ToList();
            var fieldKnown = t.Endpoints.SelectMany(Leaves).Contains(Leaf(field));
            if (entity.Length == 0 || !fieldKnown || transitions.Count == 0) { Reject("lifecycle", $"{entity}.{field}", !fieldKnown ? "its status field is not in the API" : "none of its transitions use a known endpoint"); continue; }
            var l = t.Lifecycles.FirstOrDefault(x => x.Entity.Equals(entity, StringComparison.OrdinalIgnoreCase) && x.Field.Equals(field, StringComparison.OrdinalIgnoreCase));
            if (l is null) { t.Lifecycles.Add(l = new TwinLifecycle { Entity = entity, Field = field, Status = "inferred" }); t.Changes.Understood.Add($"lifecycle {entity}.{field}"); }
            l.States = l.States.Union(Strings(o["states"])).ToList();
            l.Transitions = transitions.Select(x => new TwinTransition(x.From, x.To, x.Ep!.Key, roles.Contains(x.Role) ? x.Role : null)).ToList();
            l.Saw(v, "model", assumed ? evidence with { Note = "the model named no field; the verifier assumed status" } : evidence);
            l.Status = transitions.All(x => Succeeded(x.Ep!)) ? "verified" : "inferred";
            admitted++;
        }

        var reproposed = new List<TwinRule>();
        var storiesWithRules = new HashSet<string>();
        foreach (var o in (reply["rules"] as JsonArray ?? []).OfType<JsonObject>())
        {
            proposed++;
            var on = J.Text(o["on"]).Trim(); var check = J.Text(o["check"]).Trim();
            var statement = J.Text(o["statement"]).Trim() is { Length: > 0 } st ? st : check; // a rule without words is labelled by its check
            var label = statement.Length > 0 ? statement : check;
            var (rule, err) = InvariantRules.Create(null, on, check, statement, J.Text(o["story"]), "Model", 0);
            if (rule is null) { Reject("rule", label, $"not a valid check: {err}"); continue; }
            var ep = Endpoint(t, on);
            if (ep is null) { Reject("rule", label, $"{on} is not a known endpoint"); continue; }
            if (p.RetiredRules.Contains($"{ep.Key} {check}")) { Reject("rule", label, "a reviewer rejected this rule before"); continue; }
            var leaves = Leaves(ep);
            var missing = Identifiers(check).Where(id => !leaves.Contains(id)).ToList();
            if (missing.Count > 0) { Reject("rule", label, $"uses fields {ep.Key} does not have: {string.Join(", ", missing)}"); continue; }
            var story = J.Text(o["story"]).Trim();
            // The same check in other words ("guests <= maxGuests" and "status >= 400 || (guests <= maxGuests)") is the same
            // rule: the newer wording replaces the older one instead of checking, and failing, twice.
            var r = t.Rules.FirstOrDefault(x => x.On == ep.Key && SameCheck(x.Check, check));
            if (r is not null && r.Check != check) { r.Check = check; r.Statement = statement; }
            if (r is not null && r.Status == "stale") { r.Status = "inferred"; reproposed.Add(r); }
            if (r is null) { t.Rules.Add(r = new TwinRule { Statement = statement, On = ep.Key, Check = check, Story = stories.Contains(story) ? story : null, Status = "inferred" }); t.Changes.Understood.Add($"rule {(r.Story is null ? "" : r.Story + ": ")}{statement}"); }
            r.Saw(v, "model", evidence);
            if (r.Story is { } rs) storiesWithRules.Add(rs);
            reproposed.Add(r);
            if (r.Status != "accepted") r.Status = "inferred"; // a rule is an expectation; whether it holds is checked on responses, and a reviewer accepts it (P8)
            admitted++;
        }
        // Rules gone stale because their story changed are replaced by what the model wrote from the new criteria: a stale
        // rule the model did not propose again is dropped when it wrote other rules for that story.
        t.Rules.RemoveAll(x => x.Status == "stale" && x.Story is { } s && storiesWithRules.Contains(s) && !reproposed.Contains(x));
        return (proposed, admitted);
    }

    // Two checks are the same rule if they differ only in spacing, outer parentheses or the "status >= 400 ||" guard that
    // exempts rejected requests.
    public static bool SameCheck(string a, string b) => Canonical(a) == Canonical(b);

    static string Canonical(string check)
    {
        var s = Regex.Replace(check, @"\s+", "").Replace("'", "\"");
        s = Regex.Replace(s, @"^status>=400\|\|", "");
        while (s.StartsWith('(') && s.EndsWith(')') && Balanced(s[1..^1])) s = s[1..^1];
        return s;
    }

    static bool Balanced(string s) { var d = 0; foreach (var c in s) { d += c == '(' ? 1 : c == ')' ? -1 : 0; if (d < 0) return false; } return d == 0; }

    static readonly HashSet<string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "request", "response", "prev", "query", "and", "or", "not", "true", "false", "null", "item", "items",
        "sum", "count", "all", "any", "min", "max", "abs", "round", "len", "lower", "contains", "exists", "date", "today",
    };

    // The field names a check refers to (functions, keywords and string literals left out), as leaf names.
    public static List<string> Identifiers(string check) =>
        Ident().Matches(Regex.Replace(check, "'[^']*'|\"[^\"]*\"", " "))
            .Select(m => m.Value).Where(x => !char.IsDigit(x[0]))
            .SelectMany(x => x.Split('.')).Select(x => x.Replace("[]", "").Trim().ToLowerInvariant())
            .Where(x => x.Length > 0 && !Words.Contains(x) && !char.IsDigit(x[0])).Distinct().ToList();

    [GeneratedRegex(@"^\s*(GET|POST|PUT|PATCH|DELETE)\s+(\S+)", RegexOptions.IgnoreCase)] private static partial Regex EndpointText();
    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_.\[\]]*")] private static partial Regex Ident();
}
