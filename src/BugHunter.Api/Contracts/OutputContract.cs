using System.Text.Json.Nodes;
using BugHunter.Api.Common;

namespace BugHunter.Api.Contracts;

// The effective, enforceable output contract of one agent version, plus where it came from.
public record OutputContract(JsonObject Schema, string Source, string Hash)
{
    public List<SchemaError> Validate(JsonNode? output) => JsonSchemaValidator.Validate(output, Schema);
}

public record ParsedResponse(JsonNode? Value, string? Error);

public static class OutputContracts
{
    static string JsonType(string t) => t.ToLowerInvariant() switch
    {
        "array" => "array",
        "object" or "json" => "object",
        "number" or "float" or "double" => "number",
        "integer" or "int" => "integer",
        "boolean" or "bool" => "boolean",
        _ => "string" // string, markdown, text
    };

    // Turns the legacy key->type map into a strict JSON Schema; every declared key is required.
    public static JsonObject FromKeyTypes(Dictionary<string, string> keys)
    {
        var props = new JsonObject();
        foreach (var (k, t) in keys) props[k] = new JsonObject { ["type"] = JsonType(t) };
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["required"] = new JsonArray(keys.Keys.Select(k => (JsonNode)JsonValue.Create(k)!).ToArray())
        };
    }


    // Strict parse: the whole response, or a single fenced/embedded JSON object. Never invents a wrapper around free text.
    public static ParsedResponse Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new(null, "The model returned an empty response.");
        var t = text.Trim();
        if (TryObject(t) is { } whole) return new(whole, null);

        var fence = System.Text.RegularExpressions.Regex.Match(t, "```(?:json)?\\s*(\\{[\\s\\S]*?\\})\\s*```");
        if (fence.Success && TryObject(fence.Groups[1].Value) is { } fenced) return new(fenced, null);

        var s = t.IndexOf('{'); var e = t.LastIndexOf('}');
        if (s >= 0 && e > s && TryObject(t[s..(e + 1)]) is { } embedded) return new(embedded, null);
        return new(null, "The response is not a valid JSON object.");
    }

    static JsonObject? TryObject(string s)
    {
        try { return JsonNode.Parse(s) as JsonObject; } catch { return null; }
    }

    // Schema as sent to provider structured-output APIs: annotation keys some providers reject are removed.
    public static JsonObject ForProvider(JsonObject schema)
    {
        var c = (JsonObject)schema.DeepClone();
        c.Remove("$schema"); c.Remove("$id"); c.Remove("title");
        return c;
    }

    public static string PromptInstruction(OutputContract c) =>
        "Respond ONLY with a single JSON object (no prose, no markdown fences) that conforms to this JSON Schema:\n" + c.Schema.ToJsonString(J.Opts);

    public static string RepairInstruction(string previous, IEnumerable<string> errors) =>
        "Your previous response did not satisfy the output contract.\n\nPrevious response:\n" + (previous.Length > 6000 ? previous[..6000] : previous) +
        "\n\nValidation errors:\n" + string.Join("\n", errors.Take(25).Select(e => "- " + e)) +
        "\n\nReturn the corrected JSON object only. Keep every value that was already valid.";
}
