using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BugHunter.Api.Common;
using BugHunter.Api.Hunter;

namespace BugHunter.Api.Twin;

// An endpoint as a specification describes it.
public record DeclaredEndpoint(string Method, string Path, List<int> Statuses, List<string> RequestFields, List<string> ResponseFields, string? Summary);

public static partial class Specs
{
    // OpenAPI 3: every operation with its documented statuses and the field paths of its JSON request and success
    // response, $refs resolved, list items written as items[].field (the twin's observed field format).
    public static List<DeclaredEndpoint> OpenApi(JsonNode doc)
    {
        var list = new List<DeclaredEndpoint>();
        foreach (var (path, item) in doc["paths"] as JsonObject ?? [])
            foreach (var (method, op) in item as JsonObject ?? [])
            {
                if (method is not ("get" or "post" or "put" or "patch" or "delete") || op is not JsonObject o) continue;
                var statuses = (o["responses"] as JsonObject ?? []).Select(r => int.TryParse(r.Key, out var c) ? c : 0).Where(c => c > 0).Order().ToList();
                var request = Fields(doc, o["requestBody"]?["content"]?["application/json"]?["schema"]);
                var ok = (o["responses"] as JsonObject ?? []).FirstOrDefault(r => r.Key.StartsWith('2')).Value;
                var response = Fields(doc, ok?["content"]?["application/json"]?["schema"]);
                list.Add(new(method.ToUpperInvariant(), Template(path), statuses, request, response, J.Text(o["summary"]) is { Length: > 0 } s ? s : null));
            }
        return list;
    }

    public static string Template(string path) => Param().Replace(path, "{id}");

    static List<string> Fields(JsonNode doc, JsonNode? schema)
    {
        var fields = new List<string>();
        Walk(doc, schema, "", 0, fields);
        return fields.Distinct().Take(60).ToList();
    }

    static void Walk(JsonNode doc, JsonNode? s, string prefix, int depth, List<string> into)
    {
        if (s is null || depth > 4) return;
        if (J.Text(s["$ref"]) is { Length: > 0 } r && r.StartsWith("#/"))
        {
            JsonNode? target = doc;
            foreach (var part in r[2..].Split('/')) target = target?[part];
            Walk(doc, target, prefix, depth + 1, into);
            return;
        }
        foreach (var key in new[] { "allOf", "oneOf", "anyOf" })
            foreach (var sub in s[key] as JsonArray ?? []) Walk(doc, sub, prefix, depth + 1, into);
        var type = s["type"] is JsonArray types ? types.Select(t => J.Text(t)).FirstOrDefault(t => t != "null") : J.Text(s["type"]);
        if (type == "array" || s["items"] is not null && s["properties"] is null) { Walk(doc, s["items"], prefix + "[]", depth + 1, into); return; }
        if (s["properties"] is JsonObject props)
            foreach (var (name, p) in props)
            {
                var path = prefix.Length == 0 ? name : $"{prefix}.{name}";
                var inner = p?["$ref"] is not null ? Resolve(doc, p) : p;
                var t = inner?["type"] is JsonArray ts ? ts.Select(x => J.Text(x)).FirstOrDefault(x => x != "null") : J.Text(inner?["type"]);
                if (t is "object" or "array" || inner?["properties"] is not null || inner?["items"] is not null) Walk(doc, p, path, depth + 1, into);
                else into.Add(path);
            }
    }

    static JsonNode? Resolve(JsonNode doc, JsonNode? s)
    {
        for (var i = 0; i < 5 && J.Text(s?["$ref"]) is { Length: > 0 } r && r.StartsWith("#/"); i++)
        {
            JsonNode? target = doc;
            foreach (var part in r[2..].Split('/')) target = target?[part];
            s = target;
        }
        return s;
    }

    // HAR (recorded browser traffic): each JSON API call becomes an observation, as if a hunt had made it.
    public static List<NetworkEvent> Har(JsonNode har) =>
        (har["log"]?["entries"] as JsonArray ?? []).OfType<JsonObject>().Select(e => new NetworkEvent
        {
            Method = J.Text(e["request"]?["method"]), Url = J.Text(e["request"]?["url"]), Status = (int)(J.Num(e["response"]?["status"]) ?? 0),
            ResourceType = J.Text(e["_resourceType"]) is { Length: > 0 } rt ? rt : "fetch",
            RequestBody = e["request"]?["postData"]?["text"]?.ToString(), Body = e["response"]?["content"]?["text"]?.ToString()
        }).Where(e => e.ResourceType is "xhr" or "fetch" && e.Status != 0 && e.Url.Contains("/api", StringComparison.OrdinalIgnoreCase)).ToList();

    [GeneratedRegex(@"\{[^}]+\}")] private static partial Regex Param();
}
