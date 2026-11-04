using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BugHunter.Api.Hunter;

// Domain knowledge given to the planner during warm-up: Markdown skill packs (skills/*.md), chosen by matching their
// keywords against what the survey discovered. Files rather than code, so a team can review and extend them.
public record SkillPack(string Name, string Title, string[] Match, string Body, bool Custom = false);

public static partial class SkillPacks
{
    // The built-in packs and the team's own; a custom pack with a built-in name overrides it.
    public static List<SkillPack> LoadAll(HunterOptions opts) =>
        Load(opts.CustomSkillsDir).Select(p => p with { Custom = true }).Concat(Load(opts.SkillsDir)).DistinctBy(p => p.Name).OrderBy(p => p.Name).ToList();

    public static string Markdown(string name, string title, IEnumerable<string> match, string body) =>
        $"---\nname: {name}\ntitle: {title}\nmatch: {string.Join(", ", match)}\n---\n\n{body.Trim()}\n";

    public static List<SkillPack> Load(string? dir)
    {
        if (dir is null || !Directory.Exists(dir)) return [];
        var packs = new List<SkillPack>();
        foreach (var f in Directory.EnumerateFiles(dir, "*.md").Where(f => !Path.GetFileName(f).Equals("README.md", StringComparison.OrdinalIgnoreCase)).Order())
        {
            var text = File.ReadAllText(f);
            var m = FrontMatter().Match(text);
            if (!m.Success) continue;
            string Field(string name) => Regex.Match(m.Groups[1].Value, $@"^{name}:\s*(.+)$", RegexOptions.Multiline) is { Success: true } x ? x.Groups[1].Value.Trim() : "";
            var match = Field("match").Split(',').Select(k => k.Trim().ToLowerInvariant()).Where(k => k.Length > 0).ToArray();
            packs.Add(new(Field("name") is { Length: > 0 } n ? n : Path.GetFileNameWithoutExtension(f), Field("title"), match, text[m.Length..].Trim()));
        }
        return packs;
    }

    // A pack applies when at least two of its keywords appear in what the app showed (one word alone is too weak a signal).
    public static List<SkillPack> Select(IEnumerable<SkillPack> packs, string corpus)
    {
        var text = corpus.ToLowerInvariant();
        return packs.Select(p => (p, hits: p.Match.Count(k => Regex.IsMatch(text, $@"\b{Regex.Escape(k)}\b"))))
            .Where(x => x.hits >= 2).OrderByDescending(x => x.hits).Select(x => x.p).ToList();
    }

    // Where the packs live: Hunter:SkillsDir, else a "skills" folder above the API's content root.
    public static string? FindDir(string? configured, string contentRoot)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return Directory.Exists(configured) ? configured : null;
        for (var d = new DirectoryInfo(contentRoot); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "skills"))) return Path.Combine(d.FullName, "skills");
        return null;
    }

    [GeneratedRegex(@"^---\s*\n([\s\S]*?)\n---\s*\n")] private static partial Regex FrontMatter();
}

// The application's API as observed through the browser: every endpoint called, with the request and response fields
// and status codes seen. Built from recorded traffic (no specification needed) and kept on the app map.
public class ApiEndpoint
{
    public string Endpoint { get; set; } = "";       // "POST /api/cart/items/{id}"
    public List<int> Statuses { get; set; } = [];
    public List<string> RequestFields { get; set; } = [];
    public List<string> ResponseFields { get; set; } = [];
    public int Calls { get; set; }
}

public static class ApiCatalog
{
    public static void Learn(AppMap map, IEnumerable<NetworkEvent> events)
    {
        foreach (var e in events.Where(e => e.ResourceType is "xhr" or "fetch" && e.Status != 0))
        {
            var key = Probes.Endpoint(e);
            var api = map.Apis.FirstOrDefault(a => a.Endpoint == key);
            if (api is null) { if (map.Apis.Count >= 80) continue; map.Apis.Add(api = new ApiEndpoint { Endpoint = key }); }
            api.Calls++;
            if (!api.Statuses.Contains(e.Status)) api.Statuses.Add(e.Status);
            if (e.RequestJson() is JsonObject req) Merge(api.RequestFields, Fields(req, ""));
            if (e.Status is >= 200 and < 300 && e.Json() is { } body) Merge(api.ResponseFields, Fields(body, ""));
        }
    }

    static void Merge(List<string> into, IEnumerable<string> fields) { foreach (var f in fields) if (!into.Contains(f) && into.Count < 40) into.Add(f); }

    // Field paths with list items written as "items[].price", two levels deep.
    public static IEnumerable<string> Fields(JsonNode node, string prefix, int depth = 0)
    {
        if (depth > 2) yield break;
        if (node is JsonArray a && a.Count > 0 && a[0] is { } first) { foreach (var f in Fields(first, prefix + "[]", depth + 1)) yield return f; yield break; }
        if (node is not JsonObject o) yield break;
        foreach (var (k, v) in o)
        {
            var path = prefix.Length == 0 ? k : $"{prefix}.{k}";
            if (v is JsonObject or JsonArray) foreach (var f in Fields(v, path, depth + 1)) yield return f;
            else yield return path;
        }
    }

    public static string Describe(AppMap map, int max) =>
        string.Join("\n", map.Apis.OrderByDescending(a => a.Calls).Take(max).Select(a =>
            $"- {a.Endpoint} [{string.Join(",", a.Statuses.Order())}]" +
            (a.RequestFields.Count > 0 ? $" request: {string.Join(", ", a.RequestFields.Take(12))}" : "") +
            (a.ResponseFields.Count > 0 ? $" response: {string.Join(", ", a.ResponseFields.Take(16))}" : "")));
}
