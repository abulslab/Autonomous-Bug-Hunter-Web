using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DemoShop;

// In-memory document store with the same shape as the platform's simulator store, so the shop logic ported unchanged.
// State lives for the life of the process; the test-reset hook clears it.
public class SimStore
{
    readonly ConcurrentDictionary<(string Sim, string Kind, string Key), string> _docs = new();

    public static JsonObject Obj(object o) => JsonSerializer.SerializeToNode(o, J.Opts)!.AsObject();
    public static string Now() => DateTime.UtcNow.ToString("o");
    public static IResult Fail(string message, int status = 400) => Results.Problem(message, statusCode: status);

    static JsonObject Parse(string key, string json) { var o = JsonNode.Parse(json)!.AsObject(); o["_key"] = key; return o; }

    public Task<JsonObject?> GetAsync(string sim, string kind, string key, CancellationToken ct) =>
        Task.FromResult(_docs.TryGetValue((sim, kind, key), out var json) ? Parse(key, json) : null);

    public Task<List<JsonObject>> ListAsync(string sim, string kind, CancellationToken ct) =>
        Task.FromResult(_docs.Where(d => d.Key.Sim == sim && d.Key.Kind == kind).Select(d => Parse(d.Key.Key, d.Value)).ToList());

    public Task PutAsync(string sim, string kind, string key, JsonObject data, CancellationToken ct, bool save = true)
    {
        data.Remove("_key");
        _docs[(sim, kind, key)] = data.ToJsonString();
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string sim, string kind, string key, CancellationToken ct) => Task.FromResult(_docs.TryRemove((sim, kind, key), out _));

    public Task<int> NextAsync(string sim, string kind, int start, CancellationToken ct)
    {
        var max = _docs.Keys.Where(k => k.Sim == sim && k.Kind == kind)
            .Select(k => int.TryParse(new string(k.Key.Where(char.IsDigit).ToArray()), out var n) ? n : 0).DefaultIfEmpty(start - 1).Max();
        return Task.FromResult(Math.Max(start, max + 1));
    }

    readonly SemaphoreSlim _seed = new(1, 1);

    // Seed once; a marker record keeps it from running again until the next reset.
    public async Task EnsureSeededAsync(string sim, Func<Task> seed, CancellationToken ct)
    {
        if (_docs.ContainsKey((sim, "meta", "seeded"))) return;
        await _seed.WaitAsync(ct);
        try
        {
            if (_docs.ContainsKey((sim, "meta", "seeded"))) return;
            await seed();
            await PutAsync(sim, "meta", "seeded", new JsonObject { ["at"] = Now() }, ct);
        }
        finally { _seed.Release(); }
    }

    public Task<int> ClearAsync(string sim, CancellationToken ct)
    {
        var keys = _docs.Keys.Where(k => k.Sim == sim).ToList();
        foreach (var k in keys) _docs.TryRemove(k, out _);
        return Task.FromResult(keys.Count);
    }
}

public static class J
{
    public static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web);

    public static string Text(JsonNode? n) => n switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => n.ToJsonString(Opts)
    };
}
