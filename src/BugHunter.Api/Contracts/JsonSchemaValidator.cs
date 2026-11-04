using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BugHunter.Api.Common;

namespace BugHunter.Api.Contracts;

public record SchemaError(string Path, string Message)
{
    public override string ToString() => $"{Path}: {Message}";
}

// Deterministic JSON Schema (draft-07 subset) validator used to enforce agent output contracts.
// Keywords outside the supported set are reported, never silently treated as satisfied.
public static class JsonSchemaValidator
{
    static readonly HashSet<string> Annotations = ["$schema", "$id", "title", "description", "default", "examples", "$comment", "readOnly", "writeOnly"];
    static readonly HashSet<string> Supported =
        ["type", "properties", "required", "items", "enum", "const", "minItems", "maxItems", "minLength", "maxLength", "minimum", "maximum", "pattern", "additionalProperties", "uniqueItems"];

    public static List<SchemaError> Validate(JsonNode? value, JsonNode? schema)
    {
        var errors = new List<SchemaError>();
        Check(value, schema as JsonObject, "$", errors);
        return errors;
    }

    // Keywords in the schema that this validator cannot enforce; surfaced so contracts are never over-trusted.
    public static List<string> UnsupportedKeywords(JsonNode? schema)
    {
        var found = new SortedSet<string>();
        void Walk(JsonNode? n)
        {
            if (n is not JsonObject o) return;
            foreach (var (k, v) in o)
            {
                if (!Supported.Contains(k) && !Annotations.Contains(k)) found.Add(k);
                if (k == "properties" && v is JsonObject props) foreach (var p in props) Walk(p.Value);
                else if (k is "items" or "additionalProperties") Walk(v);
            }
        }
        Walk(schema);
        return [.. found];
    }

    static void Check(JsonNode? v, JsonObject? s, string path, List<SchemaError> errors)
    {
        if (s is null) return;

        if (s["type"] is { } t)
        {
            var types = t is JsonArray ta ? ta.Select(x => x?.GetValue<string>() ?? "").ToList() : [t.GetValue<string>()];
            if (!types.Any(type => IsType(v, type)))
            {
                errors.Add(new(path, $"expected {string.Join(" or ", types)} but got {Describe(v)}"));
                return;
            }
        }

        if (s["enum"] is JsonArray en && !en.Any(e => JsonNode.DeepEquals(e, v)))
            errors.Add(new(path, $"must be one of [{string.Join(", ", en.Select(e => e?.ToJsonString()))}] but got {v?.ToJsonString() ?? "null"}"));
        if (s.ContainsKey("const") && !JsonNode.DeepEquals(s["const"], v))
            errors.Add(new(path, $"must equal {s["const"]?.ToJsonString() ?? "null"}"));

        switch (v)
        {
            case JsonObject o:
                if (s["required"] is JsonArray req)
                    foreach (var r in req.Select(x => x?.GetValue<string>()).Where(x => x is not null))
                        if (!o.ContainsKey(r!) || o[r!] is null) errors.Add(new($"{path}.{r}", "is required"));
                var props = s["properties"] as JsonObject;
                foreach (var (k, child) in o)
                {
                    if (props?[k] is JsonObject ps) Check(child, ps, $"{path}.{k}", errors);
                    else if (s["additionalProperties"] is JsonValue ap && ap.TryGetValue<bool>(out var allowed) && !allowed)
                        errors.Add(new($"{path}.{k}", "is not allowed by the contract"));
                    else if (s["additionalProperties"] is JsonObject aps) Check(child, aps, $"{path}.{k}", errors);
                }
                break;
            case JsonArray a:
                if (J.Num(s["minItems"]) is { } mi && a.Count < mi) errors.Add(new(path, $"must contain at least {mi} item(s) but has {a.Count}"));
                if (J.Num(s["maxItems"]) is { } ma && a.Count > ma) errors.Add(new(path, $"must contain at most {ma} item(s) but has {a.Count}"));
                if (s["uniqueItems"] is JsonValue u && u.TryGetValue<bool>(out var uniq) && uniq)
                    for (var i = 0; i < a.Count; i++)
                        for (var j = i + 1; j < a.Count; j++)
                            if (JsonNode.DeepEquals(a[i], a[j])) errors.Add(new($"{path}[{j}]", $"duplicates item {i}"));
                if (s["items"] is JsonObject items)
                    for (var i = 0; i < a.Count; i++) Check(a[i], items, $"{path}[{i}]", errors);
                break;
            case JsonValue jv when jv.TryGetValue<string>(out var str):
                if (J.Num(s["minLength"]) is { } mnl && str.Length < mnl) errors.Add(new(path, $"must be at least {mnl} character(s)"));
                if (J.Num(s["maxLength"]) is { } mxl && str.Length > mxl) errors.Add(new(path, $"must be at most {mxl} character(s)"));
                if (s["pattern"] is { } pat && !Regex.IsMatch(str, pat.GetValue<string>(), RegexOptions.None, TimeSpan.FromMilliseconds(100)))
                    errors.Add(new(path, $"must match pattern {pat}"));
                break;
            case JsonValue nv when TryNumber(nv, out var num):
                if (J.Num(s["minimum"]) is { } min && num < min) errors.Add(new(path, $"must be >= {min} but is {num}"));
                if (J.Num(s["maximum"]) is { } max && num > max) errors.Add(new(path, $"must be <= {max} but is {num}"));
                break;
        }
    }

    static bool TryNumber(JsonValue v, out double d)
    {
        if (v.TryGetValue<double>(out d)) return true;
        if (v.TryGetValue<int>(out var i)) { d = i; return true; }
        if (v.TryGetValue<long>(out var l)) { d = l; return true; }
        if (v.TryGetValue<decimal>(out var m)) { d = (double)m; return true; }
        return false;
    }

    static bool IsType(JsonNode? v, string type) => type switch
    {
        "object" => v is JsonObject,
        "array" => v is JsonArray,
        "string" => v is JsonValue s && s.TryGetValue<string>(out _),
        "boolean" => v is JsonValue b && b.TryGetValue<bool>(out _),
        "number" => v is JsonValue n && !n.TryGetValue<string>(out _) && !n.TryGetValue<bool>(out _) && TryNumber(n, out _),
        "integer" => v is JsonValue n && !n.TryGetValue<string>(out _) && TryNumber(n, out var d) && Math.Abs(d % 1) < double.Epsilon,
        "null" => v is null,
        _ => true
    };

    static string Describe(JsonNode? v) => v switch
    {
        null => "null",
        JsonObject => "object",
        JsonArray => "array",
        JsonValue s when s.TryGetValue<string>(out _) => "string",
        JsonValue b when b.TryGetValue<bool>(out _) => "boolean",
        _ => "number"
    };
}
