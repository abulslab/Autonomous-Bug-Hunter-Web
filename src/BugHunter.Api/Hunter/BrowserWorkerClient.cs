using System.Net.Http.Headers;
using System.Net.Http.Json;
using BugHunter.Api.Common;

namespace BugHunter.Api.Hunter;

public class HunterOptions
{
    public string WorkerUrl { get; set; } = "http://127.0.0.1:5299";
    public string? WorkerToken { get; set; }
    public bool? AllowPrivateTargets { get; set; }
    public int MaxConcurrentHunts { get; set; } = 2;
    public int MaxMinutes { get; set; } = 20;
    public string? DemoShopUrl { get; set; }     // the demo shop with seeded defects (src/DemoShop), for benchmarks
    public string? DemoHotelUrl { get; set; }    // the demo hotel (src/DemoHotel), a second benchmark target
    public string? DemoShopKey { get; set; }     // the demo apps' admin key: defect switches and the test-reset hook
    public string MediaDir { get; set; } = "";   // where hunt recordings are kept; resolved at startup
    public bool? AutoStartWorker { get; set; }   // start a local worker from the API (default: on outside Production)
    public string? WorkerDir { get; set; }       // defaults to src/browser-worker found above the API's content root
    public string? SkillsDir { get; set; }       // skill packs; defaults to a "skills" folder above the API's content root
    public string? CustomSkillsDir { get; set; } // packs a team adds from the UI; defaults to <data>/skills (kept out of the repository)
}

public class BrowserWorkerException(string message) : Exception(message);

// Thin client for src/browser-worker. The worker runs the browser; this process never does.
public class BrowserWorkerClient(HttpClient http, HunterOptions opts)
{
    string U(string path) => opts.WorkerUrl.TrimEnd('/') + path;

    async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, U(path));
        if (body is not null) req.Content = JsonContent.Create(body, options: J.Opts);
        if (!string.IsNullOrEmpty(opts.WorkerToken)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opts.WorkerToken);
        HttpResponseMessage res;
        try { res = await http.SendAsync(req, ct); }
        catch (HttpRequestException e) { throw new BrowserWorkerException($"Browser worker is not reachable at {opts.WorkerUrl} ({e.Message}). Start it with: cd src/browser-worker && npm start"); }
        using (res)
        {
            if (!res.IsSuccessStatusCode)
            {
                string? err = null;
                try { err = J.Text(System.Text.Json.Nodes.JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))?["error"]); } catch (System.Text.Json.JsonException) { /* not JSON */ }
                throw new BrowserWorkerException(string.IsNullOrEmpty(err) ? $"Browser worker returned {(int)res.StatusCode}." : err);
            }
            return (await res.Content.ReadFromJsonAsync<T>(J.Opts, ct))!;
        }
    }

    public async Task<(bool Ok, string? Error, int Sessions)> HealthAsync(CancellationToken ct)
    {
        var (ok, error, sessions, _) = await HealthBuildAsync(ct);
        return (ok, error, sessions);
    }

    public async Task<(bool Ok, string? Error, int Sessions, string? Build)> HealthBuildAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromSeconds(3));
            var h = await SendAsync<Dictionary<string, System.Text.Json.JsonElement>>(HttpMethod.Get, "/healthz", null, cts.Token);
            return (true, null, h.TryGetValue("sessions", out var s) ? s.GetInt32() : 0, h.TryGetValue("build", out var b) ? b.GetString() : null);
        }
        catch (Exception e) when (e is BrowserWorkerException or OperationCanceledException) { return (false, e is OperationCanceledException ? "Browser worker did not respond." : e.Message, 0, null); }
    }

    public async Task ShutdownAsync(CancellationToken ct)
    {
        try { await SendAsync<System.Text.Json.Nodes.JsonObject>(HttpMethod.Post, "/shutdown", new { }, ct); } catch (Exception e) when (e is BrowserWorkerException or System.Text.Json.JsonException) { /* already gone */ }
    }

    // fast: a replay session with no recording and no cursor pauses.
    public async Task<(string Id, PageSnapshot Snapshot)> OpenAsync(string url, IEnumerable<string> allowedHosts, Dictionary<string, string> secrets, CancellationToken ct, bool fast = false)
    {
        var node = await SendAsync<System.Text.Json.Nodes.JsonObject>(HttpMethod.Post, "/sessions", new { url, allowedHosts, secrets, fast, screenshot = !fast }, ct);
        var id = J.Text(node["id"]);
        return (id, System.Text.Json.JsonSerializer.Deserialize<PageSnapshot>(node, J.Opts)!);
    }

    public Task<ActResult> ActAsync(string session, HuntAction a, bool snapshot, CancellationToken ct, bool screenshot = true) =>
        SendAsync<ActResult>(HttpMethod.Post, $"/sessions/{session}/act", new { a.Type, a.Ref, a.Value, a.Url, a.Key, snapshot, screenshot = snapshot && screenshot }, ct);

    // Resends a recorded request (by its rid) inside the browser session; body null keeps the recorded body.
    // method: another method than the recorded call's; then, without a body of its own, no body is sent.
    public Task<NetworkEvent> RequestAsync(string session, int rid, string? url, string? body, bool dropAuth, CancellationToken ct, string? method = null)
    {
        var o = new System.Text.Json.Nodes.JsonObject { ["rid"] = rid, ["dropAuth"] = dropAuth };
        if (url is not null) o["url"] = url;
        if (body is not null) o["body"] = body;
        if (method is not null) { o["method"] = method; if (body is null) o["body"] = null; }
        return SendAsync<NetworkEvent>(HttpMethod.Post, $"/sessions/{session}/request", o, ct);
    }

    public Task<PageSnapshot> SnapshotAsync(string session, CancellationToken ct, bool screenshot = true) =>
        SendAsync<PageSnapshot>(HttpMethod.Post, $"/sessions/{session}/snapshot", new { screenshot }, ct);

    // Live MJPEG stream of the session's page; the caller copies it through and disposes the response.
    public async Task<HttpResponseMessage> StreamAsync(string session, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, U($"/sessions/{session}/stream"));
        if (!string.IsNullOrEmpty(opts.WorkerToken)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opts.WorkerToken);
        var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode) { res.Dispose(); throw new BrowserWorkerException("The live view for this session is not available."); }
        return res;
    }

    // The session's WebM recording; available for a while after the session is closed.
    public async Task<bool> SaveVideoAsync(string session, string file, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, U($"/videos/{session}"));
            if (!string.IsNullOrEmpty(opts.WorkerToken)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opts.WorkerToken);
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await using var fs = File.Create(file);
            await res.Content.CopyToAsync(fs, ct);
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException) { return false; }
    }

    // Marks the session as in use. A hunt waiting on slow model calls sends no browser actions, and the worker closes
    // sessions idle for ten minutes.
    public async Task TouchAsync(string session, CancellationToken ct)
    {
        try { using var req = new HttpRequestMessage(HttpMethod.Post, U($"/sessions/{session}/touch")); if (!string.IsNullOrEmpty(opts.WorkerToken)) req.Headers.Authorization = new("Bearer", opts.WorkerToken); (await http.SendAsync(req, ct)).Dispose(); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { /* the next real call reports a lost session */ }
    }

    public async Task CloseAsync(string session)
    {
        try { using var req = new HttpRequestMessage(HttpMethod.Delete, U($"/sessions/{session}")); if (!string.IsNullOrEmpty(opts.WorkerToken)) req.Headers.Authorization = new("Bearer", opts.WorkerToken); (await http.SendAsync(req)).Dispose(); }
        catch { /* the worker expires idle sessions on its own */ }
    }
}
