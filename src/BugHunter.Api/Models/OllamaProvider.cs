using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BugHunter.Api.Common;

namespace BugHunter.Api.Models;

public record OllamaHealth(string Status, string Version, List<string> Models, long LatencyMs, string Endpoint, bool IsAvailable);

public class OllamaProvider : IModelProvider
{
    private readonly HttpClient _http;
    private static readonly string[] SupportedProviders = ["Ollama", "Local"];
    public string DefaultEndpoint { get; set; } = "http://localhost:11434";
    public int ContextTokens { get; set; } = 16384; // Ollama's default window is a few thousand tokens and drops the rest silently

    public OllamaProvider(HttpClient http)
    {
        _http = http;
        _http.Timeout = Timeout.InfiniteTimeSpan; // per-call deadlines come from the agent runtime settings
    }

    public bool Supports(string provider) =>
        SupportedProviders.Contains(provider, StringComparer.OrdinalIgnoreCase);

    public async Task<OllamaHealth> CheckHealthAsync(string? endpoint = null, CancellationToken ct = default)
    {
        var baseUri = (endpoint ?? DefaultEndpoint).TrimEnd('/');
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            var res = await _http.GetAsync($"{baseUri}/api/tags", linkedCts.Token);
            sw.Stop();
            if (!res.IsSuccessStatusCode)
            {
                return new OllamaHealth("Degraded", "unknown", [], sw.ElapsedMilliseconds, baseUri, false);
            }

            var content = await res.Content.ReadAsStringAsync(ct);
            var json = JsonNode.Parse(content);
            var models = new List<string>();
            if (json?["models"] is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    if (item?["name"]?.GetValue<string>() is { } name)
                        models.Add(name);
                }
            }

            string version = "0.1.x";
            try
            {
                var vRes = await _http.GetAsync($"{baseUri}/api/version", ct);
                if (vRes.IsSuccessStatusCode)
                {
                    var vJson = JsonNode.Parse(await vRes.Content.ReadAsStringAsync(ct));
                    version = vJson?["version"]?.GetValue<string>() ?? version;
                }
            }
            catch { /* non-critical */ }

            return new OllamaHealth("Healthy", version, models, sw.ElapsedMilliseconds, baseUri, true);
        }
        catch (Exception)
        {
            sw.Stop();
            return new OllamaHealth("Offline", "none", [], sw.ElapsedMilliseconds, baseUri, false);
        }
    }

    public async Task<bool> IsModelAvailableAsync(string modelName, string? endpoint = null, CancellationToken ct = default)
    {
        var health = await CheckHealthAsync(endpoint, ct);
        if (!health.IsAvailable) return false;
        return health.Models.Any(m => m.Equals(modelName, StringComparison.OrdinalIgnoreCase) ||
                                      m.StartsWith(modelName + ":", StringComparison.OrdinalIgnoreCase) ||
                                      modelName.StartsWith(m.Split(':')[0], StringComparison.OrdinalIgnoreCase));
    }

    // A live local call either returns the model's answer or fails loudly; it never substitutes generated data.
    public async Task<ChatResponse> CompleteAsync(ResolvedModel m, ChatRequest r, CancellationToken ct)
    {
        var baseUrl = string.IsNullOrWhiteSpace(m.Endpoint) ? DefaultEndpoint : m.Endpoint.TrimEnd('/');
        var model = string.IsNullOrWhiteSpace(m.Model) ? "llama3.1:8b" : m.Model;
        // The native endpoint, not the OpenAI-compatible one: only it accepts the context size.
        var body = new JsonObject
        {
            ["model"] = model,
            ["stream"] = false,
            ["options"] = new JsonObject { ["temperature"] = r.Temperature, ["num_predict"] = Math.Max(r.MaxTokens, 500), ["num_ctx"] = ContextTokens },
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = r.System },
                new JsonObject { ["role"] = "user", ["content"] = r.User }
            )
        };
        if (r.JsonSchema is not null) body["format"] = "json";

        HttpResponseMessage res;
        try
        {
            // Timeout is owned by the caller (agent runtime settings); local models routinely need more than a few seconds.
            res = await Post($"{baseUrl}/api/chat", body, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderExecutionException($"Provider Error: Ollama (Local) is not reachable at {baseUrl} ({ex.Message}). Start Ollama or choose another provider.", "Ollama", null, retryable: true);
        }
        using (res)
        {
            var raw = await res.Content.ReadAsStringAsync(ct);
            if ((int)res.StatusCode == 404)
                throw new ProviderExecutionException($"Provider Error: Ollama (Local): model '{model}' is not installed. Run `ollama pull {model}`.", "Ollama", 404);
            if (!res.IsSuccessStatusCode) throw ProviderErrors.From(m, (int)res.StatusCode, raw);
            var json = JsonNode.Parse(raw);
            var text = J.Text(J.Get(json, "message.content"));
            var tin = json?["prompt_eval_count"]?.GetValue<int>() ?? (r.System.Length + r.User.Length) / 4;
            var tout = json?["eval_count"]?.GetValue<int>() ?? text.Length / 4;
            return new ChatResponse(text, tin, tout, model, J.Text(json?["done_reason"]) == "length");
        }
    }

    Task<HttpResponseMessage> Post(string url, JsonObject body, CancellationToken ct) =>
        _http.SendAsync(new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") }, ct);

    // Ollama's OpenAI-compatible endpoint streams server-sent events; deltas are yielded as the local model produces them.
    public async IAsyncEnumerable<string> StreamAsync(ResolvedModel m, ChatRequest r, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var baseUrl = string.IsNullOrWhiteSpace(m.Endpoint) ? DefaultEndpoint : m.Endpoint.TrimEnd('/');
        var body = new JsonObject
        {
            ["model"] = string.IsNullOrWhiteSpace(m.Model) ? "llama3.1:8b" : m.Model, ["temperature"] = r.Temperature, ["stream"] = true,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = r.System }, new JsonObject { ["role"] = "user", ["content"] = r.User })
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/chat/completions") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        await foreach (var data in Sse.ReadAsync(_http, req, m, ct))
        {
            if (data == "[DONE]") yield break;
            var delta = J.Text(J.Get(Sse.TryParse(data), "choices.0.delta.content"));
            if (delta.Length > 0) yield return delta;
        }
    }
}
