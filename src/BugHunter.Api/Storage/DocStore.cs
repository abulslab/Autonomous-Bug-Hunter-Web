using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using BugHunter.Api.Common;

namespace BugHunter.Api.Storage;

// Hunts, screenshots and app maps are independent JSON documents addressed by (area, kind, key), so storage is a small
// document-store contract with interchangeable backends: local files for a single node, PostgreSQL (JSONB) for a team.
public interface IDocStore
{
    Task<JsonObject?> GetAsync(string area, string kind, string key, CancellationToken ct);
    Task<List<JsonObject>> ListAsync(string area, string kind, CancellationToken ct);
    Task PutAsync(string area, string kind, string key, JsonObject data, CancellationToken ct);
    Task DeleteAsync(string area, string kind, string key, CancellationToken ct);
    string Describe { get; } // for /readyz and logs, without credentials
}

public static class Docs
{
    public static JsonObject Obj(object o) => JsonSerializer.SerializeToNode(o, J.Opts)!.AsObject();
}

// One file per document under {root}/{area}/{kind}/{key}.json.
public class FileDocStore : IDocStore
{
    readonly string _root;
    readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public FileDocStore(string root) { _root = root; Directory.CreateDirectory(root); }

    public string Describe => $"files at {_root}";

    string Dir(string area, string kind) => Path.Combine(_root, Safe(area), Safe(kind));
    string File(string area, string kind, string key) => Path.Combine(Dir(area, kind), Safe(key) + ".json");
    static string Safe(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));

    public async Task<JsonObject?> GetAsync(string area, string kind, string key, CancellationToken ct)
    {
        var f = File(area, kind, key);
        if (!System.IO.File.Exists(f)) return null;
        var gate = _locks.GetOrAdd(f, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try { var o = JsonNode.Parse(await System.IO.File.ReadAllTextAsync(f, ct))!.AsObject(); o["_key"] = key; return o; }
        finally { gate.Release(); }
    }

    public async Task<List<JsonObject>> ListAsync(string area, string kind, CancellationToken ct)
    {
        var dir = Dir(area, kind);
        if (!Directory.Exists(dir)) return [];
        var list = new List<JsonObject>();
        foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
            if (await GetAsync(area, kind, Path.GetFileNameWithoutExtension(f), ct) is { } o) list.Add(o);
        return list;
    }

    public async Task PutAsync(string area, string kind, string key, JsonObject data, CancellationToken ct)
    {
        data.Remove("_key");
        var f = File(area, kind, key);
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        var gate = _locks.GetOrAdd(f, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Write then move, so a reader never sees half a document.
            var tmp = f + ".tmp";
            await System.IO.File.WriteAllTextAsync(tmp, data.ToJsonString(), ct);
            System.IO.File.Move(tmp, f, overwrite: true);
        }
        finally { gate.Release(); }
    }

    public Task DeleteAsync(string area, string kind, string key, CancellationToken ct)
    {
        var f = File(area, kind, key);
        if (System.IO.File.Exists(f)) System.IO.File.Delete(f);
        return Task.CompletedTask;
    }
}
