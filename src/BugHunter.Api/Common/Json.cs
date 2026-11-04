using System.Text.Json;
using System.Text.Json.Nodes;

namespace BugHunter.Api.Common;

public static class J
{
    public static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public static T D<T>(string? json) where T : new() =>
        string.IsNullOrWhiteSpace(json) ? new T() : JsonSerializer.Deserialize<T>(json, Opts) ?? new T();

    // Walks "a.b.0.c" paths through a JsonNode tree.
    public static JsonNode? Get(JsonNode? root, string path)
    {
        var cur = root;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (cur is JsonObject o) cur = o[part];
            else if (cur is JsonArray a && int.TryParse(part, out var i) && i >= 0 && i < a.Count) cur = a[i];
            else return null;
        }
        return cur;
    }

    // Numeric value regardless of how the node was created (parsed JSON, int, long, double or decimal).
    public static double? Num(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<decimal>(out var m)) return (double)m;
        if (v.TryGetValue<float>(out var f)) return f;
        return null;
    }

    public static string Text(JsonNode? n) => n switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => n.ToJsonString(Opts)
    };
}
