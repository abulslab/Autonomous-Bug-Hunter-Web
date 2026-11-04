using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BugHunter.Api.Common;

namespace BugHunter.Api.Models;

// Provider adapters: one call shape (system + user prompt, optional JSON Schema) for every vendor.
public record ChatRequest(string System, string User, double Temperature, int MaxTokens,
    Dictionary<string, string> OutputSchema, string AgentName, JsonNode? Input, JsonObject? JsonSchema = null);
// Truncated: the provider stopped at the token limit, so the text (usually JSON) is cut off.
public record ChatResponse(string Text, int TokensIn, int TokensOut, string Model, bool Truncated = false);
public record ResolvedModel(string? ConnectionId, string Provider, string Endpoint, string Model, string ApiKey, bool AllowPrivate = false, string ExecutionMode = "Simulation", string FallbackPolicy = "Disabled", string ValidationStatus = "SimulatedDeterministic", bool FallbackTriggered = false, string? OriginalProvider = null);

// Live-provider failure that must surface to the user; Retryable is false for auth/config errors so retries are not wasted.
public class ProviderExecutionException(string message, string? provider = null, int? statusCode = null, bool retryable = false) : Exception(message)
{
    public string? Provider { get; } = provider;
    public int? StatusCode { get; } = statusCode;
    public bool Retryable { get; } = retryable;
}

public static class ProviderErrors
{
    public static string Display(string p) => p switch
    {
        "Gemini" => "Google Gemini", "Claude" or "Anthropic" => "Anthropic Claude", "AzureOpenAI" => "Azure OpenAI", "Ollama" => "Ollama (Local)", _ => p
    };

    public static ProviderExecutionException From(ResolvedModel m, int status, string raw)
    {
        string? msg = null;
        try { var j = JsonNode.Parse(raw); msg = J.Text(j?["error"]?["message"] ?? (j as JsonArray)?[0]?["error"]?["message"] ?? j?["error"]); } catch { /* body is not JSON */ }
        if (msg is { Length: > 300 }) msg = msg[..300];
        var reason = status switch { 401 => "401 Unauthorized", 403 => "403 Forbidden", 404 => "404 Not Found", 429 => "429 Too Many Requests", _ => status.ToString() };
        var badKey = status is 401 or 403 || (status == 400 && msg?.Contains("API key", StringComparison.OrdinalIgnoreCase) == true);
        return new ProviderExecutionException($"Provider Error: {Display(m.Provider)}: {reason}{(badKey ? ": Invalid API Key" : "")}{(string.IsNullOrWhiteSpace(msg) ? "" : " (" + msg + ")")}",
            m.Provider, status, status is 408 or 429 or >= 500);
    }
}

public interface IModelProvider
{
    bool Supports(string provider);
    Task<ChatResponse> CompleteAsync(ResolvedModel m, ChatRequest r, CancellationToken ct);
    IAsyncEnumerable<string> StreamAsync(ResolvedModel m, ChatRequest r, CancellationToken ct);
}


public static class AdcHelper
{
    private static string? _cachedToken;
    private static DateTime _tokenExpiry = DateTime.MinValue;
    private static readonly SemaphoreSlim _lock = new(1, 1);

    public static string? FindAdcPath()
    {
        var envPath = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
        if (!string.IsNullOrEmpty(envPath) && File.Exists(envPath)) return envPath;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var winPath = Path.Combine(appData, "gcloud", "application_default_credentials.json");
        if (File.Exists(winPath)) return winPath;

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var nixPath = Path.Combine(userProfile, ".config", "gcloud", "application_default_credentials.json");
        if (File.Exists(nixPath)) return nixPath;

        return null;
    }

    public static bool HasAdc() => FindAdcPath() != null;

    public static async Task<(string AccessToken, string QuotaProject)?> GetAccessTokenAsync(HttpClient http, CancellationToken ct = default)
    {
        var path = FindAdcPath();
        if (path == null) return null;

        await _lock.WaitAsync(ct);
        try
        {
            var text = await File.ReadAllTextAsync(path, ct);
            var creds = JsonNode.Parse(text);
            var quotaProject = creds?["quota_project_id"]?.ToString() ?? "default";

            if (!string.IsNullOrEmpty(_cachedToken) && DateTime.UtcNow < _tokenExpiry.AddSeconds(-60))
            {
                return (_cachedToken, quotaProject);
            }

            var clientId = creds?["client_id"]?.ToString();
            var clientSecret = creds?["client_secret"]?.ToString();
            var refreshToken = creds?["refresh_token"]?.ToString();

            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(refreshToken)) return null;

            var body = new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret ?? "",
                ["refresh_token"] = refreshToken,
                ["grant_type"] = "refresh_token"
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token")
            {
                Content = new FormUrlEncodedContent(body)
            };

            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var resText = await resp.Content.ReadAsStringAsync(ct);
            var resJson = JsonNode.Parse(resText);
            _cachedToken = resJson?["access_token"]?.ToString();
            var expiresIn = resJson?["expires_in"]?.GetValue<int>() ?? 3600;
            _tokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn);

            if (string.IsNullOrEmpty(_cachedToken)) return null;
            return (_cachedToken, quotaProject);
        }
        catch
        {
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }
}


public class OpenAiCompatibleProvider(HttpClient http) : IModelProvider
{
    // Ollama and Local go to OllamaProvider (its native /api/chat), never here.
    static readonly string[] Names = ["OpenAI", "AzureOpenAI", "Mistral", "Llama", "Gemini", "Custom"];
    public bool Supports(string provider) => Names.Contains(provider);

    // Gemini 1.x and 2.0 answer directly; 2.5 and every later generation spend tokens on thinking first.
    public static bool ThinkingGemini(string model) => !System.Text.RegularExpressions.Regex.IsMatch(model, @"gemini-(1\.|2\.0)");

    static string GeminiBase(string b) => b == "" ? "https://generativelanguage.googleapis.com/v1beta/openai" : b.EndsWith("/openai") ? b : b.EndsWith("/v1beta") ? b + "/openai" : b;

    public async Task<ChatResponse> CompleteAsync(ResolvedModel m, ChatRequest r, CancellationToken ct)
    {
        var (url, headerName, headerValue, body) = await BuildAsync(m, r, ct);
        if (r.JsonSchema is not null)
            body["response_format"] = m.Provider is "OpenAI" or "AzureOpenAI" or "Gemini"
                ? new JsonObject { ["type"] = "json_schema", ["json_schema"] = new JsonObject { ["name"] = "agent_output", ["schema"] = BugHunter.Api.Contracts.OutputContracts.ForProvider(r.JsonSchema), ["strict"] = false } }
                : new JsonObject { ["type"] = "json_object" };
        var (status, raw) = await Send(url, headerName, headerValue, body, ct);
        if (status == 400 && body.Remove("response_format"))
            (status, raw) = await Send(url, headerName, headerValue, body, ct); // endpoint does not support structured output; the contract validator still applies
        if (status is < 200 or > 299) throw ProviderErrors.From(m, status, raw);
        var json = JsonNode.Parse(raw);
        return new ChatResponse(J.Text(J.Get(json, "choices.0.message.content")),
            json?["usage"]?["prompt_tokens"]?.GetValue<int>() ?? 0, json?["usage"]?["completion_tokens"]?.GetValue<int>() ?? 0, m.Model,
            J.Text(J.Get(json, "choices.0.finish_reason")) == "length");
    }

    async Task<(string Url, string HeaderName, string HeaderValue, JsonObject Body)> BuildAsync(ResolvedModel m, ChatRequest r, CancellationToken ct)
    {
        var baseUrl = m.Endpoint.TrimEnd('/');
        string url;
        string headerName;
        string headerValue;

        var body = new JsonObject
        {
            ["model"] = m.Model,
            ["temperature"] = r.Temperature,
            ["max_tokens"] = r.MaxTokens,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = r.System },
                new JsonObject { ["role"] = "user", ["content"] = r.User }
            )
        };

        if (m.Provider == "Gemini")
        {
            // Gemini 2.5 and later think before they answer and the thinking counts against max_tokens: keep it short and
            // leave room, or long JSON replies (strategy, rules) are cut off.
            if (ThinkingGemini(m.Model))
            {
                body["reasoning_effort"] = "low";
                // Gemini 3 thinks far longer than 2.5 even at low effort (measured: ~4,300 output tokens per call).
                body["max_tokens"] = r.MaxTokens + (m.Model.Contains("gemini-2.5") ? 2048 : 8192);
            }
            if (!string.IsNullOrWhiteSpace(m.ApiKey))
            {
                url = $"{GeminiBase(baseUrl)}/chat/completions";
                headerName = "Authorization";
                headerValue = "Bearer " + m.ApiKey;
            }
            else
            {
                var adc = await AdcHelper.GetAccessTokenAsync(http, ct);
                if (adc != null)
                {
                    var (accessToken, quotaProject) = adc.Value;
                    var modelName = m.Model.StartsWith("google/") ? m.Model : $"google/{m.Model}";
                    url = $"https://aiplatform.googleapis.com/v1beta1/projects/{quotaProject}/locations/global/endpoints/openapi/chat/completions";
                    headerName = "Authorization";
                    headerValue = "Bearer " + accessToken;
                    body["model"] = modelName;
                }
                else
                {
                    url = $"{GeminiBase(baseUrl)}/chat/completions";
                    headerName = "Authorization";
                    headerValue = "Bearer " + m.ApiKey;
                }
            }
        }
        else
        {
            (url, headerName, headerValue) = m.Provider switch
            {
                "AzureOpenAI" => ($"{baseUrl}/openai/deployments/{m.Model}/chat/completions?api-version=2024-06-01", "api-key", m.ApiKey),
                "Mistral" => ($"{(baseUrl == "" ? "https://api.mistral.ai/v1" : baseUrl)}/chat/completions", "Authorization", "Bearer " + m.ApiKey),
                "Llama" => ($"{baseUrl}/chat/completions", "Authorization", "Bearer " + m.ApiKey),
                _ => ($"{(baseUrl == "" ? "https://api.openai.com/v1" : baseUrl)}/chat/completions", "Authorization", "Bearer " + m.ApiKey)
            };
        }

        if (await UrlGuard.CheckAsync(url, m.AllowPrivate, m.AllowPrivate) is { } blocked) throw new InvalidOperationException(blocked);
        return (url, headerName, headerValue, body);
    }

    async Task<(int Status, string Raw)> Send(string url, string headerName, string headerValue, JsonObject body, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            req.Headers.TryAddWithoutValidation(headerName, headerValue);
            using var res = await http.SendAsync(req, ct);
            var status = (int)res.StatusCode;
            var raw = await res.Content.ReadAsStringAsync(ct);
            if (attempt == 0 && (status is 429 or 502 or 503 or 504) && !ct.IsCancellationRequested)
            {
                await Task.Delay(1200, ct);
                continue;
            }
            return (status, raw);
        }
        return (500, "");
    }

    // Real server-sent-event streaming: deltas are yielded as the provider produces them.
    public async IAsyncEnumerable<string> StreamAsync(ResolvedModel m, ChatRequest r, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var (url, headerName, headerValue, body) = await BuildAsync(m, r, ct);
        body["stream"] = true;
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.TryAddWithoutValidation(headerName, headerValue);
        await foreach (var data in Sse.ReadAsync(http, req, m, ct))
        {
            if (data == "[DONE]") yield break;
            var delta = J.Text(J.Get(Sse.TryParse(data), "choices.0.delta.content"));
            if (delta.Length > 0) yield return delta;
        }
    }
}

// Minimal SSE reader shared by the streaming providers: yields the payload of each "data:" line.
public static class Sse
{
    public static async IAsyncEnumerable<string> ReadAsync(HttpClient http, HttpRequestMessage req, ResolvedModel m, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode) throw ProviderErrors.From(m, (int)res.StatusCode, await res.Content.ReadAsStringAsync(ct));
        using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(ct));
        while (await reader.ReadLineAsync(ct) is { } line)
            if (line.StartsWith("data:", StringComparison.Ordinal)) yield return line[5..].Trim();
    }

    public static JsonNode? TryParse(string data)
    {
        try { return JsonNode.Parse(data); } catch (JsonException) { return null; }
    }
}

public class AnthropicProvider(HttpClient http) : IModelProvider
{
    public bool Supports(string provider) => provider is "Anthropic" or "Claude";

    public async Task<ChatResponse> CompleteAsync(ResolvedModel m, ChatRequest r, CancellationToken ct)
    {
        var url = (m.Endpoint == "" ? "https://api.anthropic.com" : m.Endpoint.TrimEnd('/')) + "/v1/messages";
        var body = new JsonObject
        {
            ["model"] = m.Model, ["max_tokens"] = r.MaxTokens, ["temperature"] = r.Temperature, ["system"] = r.System,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = r.User })
        };
        if (await UrlGuard.CheckAsync(url, m.AllowPrivate, m.AllowPrivate) is { } blocked) throw new InvalidOperationException(blocked);
        if (r.JsonSchema is not null)
        {
            // Structured output via a forced tool call: the tool input is the agent output.
            body["tools"] = new JsonArray(new JsonObject { ["name"] = "submit_output", ["description"] = "Submit the final output that satisfies the contract.", ["input_schema"] = BugHunter.Api.Contracts.OutputContracts.ForProvider(r.JsonSchema) });
            body["tool_choice"] = new JsonObject { ["type"] = "tool", ["name"] = "submit_output" };
        }
        var (status, raw) = await Send(url, m.ApiKey, body, ct);
        if (status == 400 && body.Remove("tools") && body.Remove("tool_choice"))
            (status, raw) = await Send(url, m.ApiKey, body, ct);
        if (status is < 200 or > 299) throw ProviderErrors.From(m, status, raw);
        var json = JsonNode.Parse(raw);
        var toolUse = (json?["content"] as JsonArray)?.FirstOrDefault(c => J.Text(c?["type"]) == "tool_use")?["input"];
        var text = toolUse is not null ? toolUse.ToJsonString() : string.Concat((json?["content"] as JsonArray ?? []).Where(c => J.Text(c?["type"]) == "text").Select(c => J.Text(c?["text"])));
        return new ChatResponse(text,
            json?["usage"]?["input_tokens"]?.GetValue<int>() ?? 0, json?["usage"]?["output_tokens"]?.GetValue<int>() ?? 0, m.Model,
            J.Text(json?["stop_reason"]) == "max_tokens");
    }

    async Task<(int Status, string Raw)> Send(string url, string apiKey, JsonObject body, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            req.Headers.Add("x-api-key", apiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");
            using var res = await http.SendAsync(req, ct);
            var status = (int)res.StatusCode;
            var raw = await res.Content.ReadAsStringAsync(ct);
            if (attempt == 0 && (status is 429 or 502 or 503 or 504) && !ct.IsCancellationRequested)
            {
                await Task.Delay(1200, ct);
                continue;
            }
            return (status, raw);
        }
        return (500, "");
    }

    public async IAsyncEnumerable<string> StreamAsync(ResolvedModel m, ChatRequest r, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var url = (m.Endpoint == "" ? "https://api.anthropic.com" : m.Endpoint.TrimEnd('/')) + "/v1/messages";
        if (await UrlGuard.CheckAsync(url, m.AllowPrivate, m.AllowPrivate) is { } blocked) throw new InvalidOperationException(blocked);
        var body = new JsonObject
        {
            ["model"] = m.Model, ["max_tokens"] = r.MaxTokens, ["temperature"] = r.Temperature, ["system"] = r.System, ["stream"] = true,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = r.User })
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Add("x-api-key", m.ApiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        await foreach (var data in Sse.ReadAsync(http, req, m, ct))
        {
            var ev = Sse.TryParse(data);
            if (J.Text(ev?["type"]) == "message_stop") yield break;
            if (J.Text(ev?["type"]) == "content_block_delta" && J.Text(ev?["delta"]?["text"]) is { Length: > 0 } text) yield return text;
        }
    }
}


// Resolves "provider:model" selections against keys from configuration or environment variables. No database, no tenants:
// "ollama:qwen2.5:7b", "anthropic:claude-sonnet-4-5", "gemini:gemini-2.5-flash", "openai:gpt-4.1-mini".
public class ModelOptions
{
    public string Default { get; set; } = "gemini:gemini-2.5-flash";
    public string? Planner { get; set; }            // optional stronger model for the few planning calls
    public double MaxCostUsd { get; set; } = 1.0;   // default AI budget per hunt
    public string OllamaUrl { get; set; } = "http://localhost:11434";
    public int OllamaContextTokens { get; set; } = 16384; // context window for local models
    public string? AnthropicApiKey { get; set; }
    public string? GeminiApiKey { get; set; }
    public string? OpenAiApiKey { get; set; }
}

public class ModelGateway(IEnumerable<IModelProvider> providers, ModelOptions opts)
{
    public static (double In, double Out) PricePer1K(string model) => model.ToLowerInvariant() switch
    {
        var s when s.Contains("ollama") || s.Contains("llama") || s.Contains("qwen") || s.Contains("phi") || s.Contains("deepseek") || s.Contains("local") => (0.0, 0.0),
        var s when s.Contains("gpt-4.1") || s.Contains("gpt-4o") => (0.002, 0.008),
        var s when s.Contains("haiku") => (0.001, 0.005),
        var s when s.Contains("opus") => (0.015, 0.075),
        var s when s.Contains("claude") => (0.003, 0.015),
        var s when s.Contains("flash") => (0.0003, 0.0025),
        var s when s.Contains("gemini") => (0.00125, 0.01),
        _ => (0.001, 0.003)
    };

    public static double Cost(string model, int tin, int tout) { var p = PricePer1K(model); return tin / 1000.0 * p.In + tout / 1000.0 * p.Out; }

    public string DefaultSelection => opts.Default;
    public double DefaultBudget => opts.MaxCostUsd;
    // The planner for a hunt: its own choice, else the configured planner, else none (the driver plans).
    public string? PlannerSelection(string? requested) => !string.IsNullOrWhiteSpace(requested) ? requested.Trim() : string.IsNullOrWhiteSpace(opts.Planner) ? null : opts.Planner;

    static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(v)) return v;
        if (OperatingSystem.IsWindows())
        {
            v = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
            if (!string.IsNullOrWhiteSpace(v)) return v;
            v = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }
        return null;
    }

    string? Key(string? configured, string env) => !string.IsNullOrWhiteSpace(configured) ? configured : Env(env);

    public object ProviderStatus() => new
    {
        gemini = Key(opts.GeminiApiKey, "GEMINI_API_KEY") is { Length: > 0 } || AdcHelper.HasAdc(),
        anthropic = Key(opts.AnthropicApiKey, "ANTHROPIC_API_KEY") is { Length: > 0 },
        openai = Key(opts.OpenAiApiKey, "OPENAI_API_KEY") is { Length: > 0 },
        ollamaUrl = opts.OllamaUrl
    };

    // The models this installation can call right now (keys present), for the launch form.
    public List<(string Id, string Label)> Configured() =>
    [
        .. Key(opts.AnthropicApiKey, "ANTHROPIC_API_KEY") is { Length: > 0 } ? new[] { ("anthropic:claude-sonnet-4-5", "Claude Sonnet 4.5"), ("anthropic:claude-haiku-4-5", "Claude Haiku 4.5") } : [],
        .. Key(opts.GeminiApiKey, "GEMINI_API_KEY") is { Length: > 0 } || AdcHelper.HasAdc()
            ? new[] { ("gemini:gemini-2.5-flash", "Gemini 2.5 Flash"), ("gemini:gemini-2.5-pro", "Gemini 2.5 Pro"), ("gemini:gemini-2.5-flash-lite", "Gemini 2.5 Flash-Lite") } : [],
        .. Key(opts.OpenAiApiKey, "OPENAI_API_KEY") is { Length: > 0 } ? new[] { ("openai:gpt-4.1-mini", "GPT-4.1 mini") } : [],
    ];

    public Task<ResolvedModel> ResolveAsync(string? connection, CancellationToken ct, string risk = "Low")
    {
        var sel = string.IsNullOrWhiteSpace(connection) ? opts.Default : connection.Trim();
        var i = sel.IndexOf(':');
        var (provider, model) = i < 0 ? (sel, "") : (sel[..i].ToLowerInvariant(), sel[(i + 1)..]);
        ResolvedModel R(string type, string endpoint, string defaultModel, string key) =>
            new(sel, type, endpoint, model.Length > 0 ? model : defaultModel, key, type == "Ollama", "Live", "Disabled", "VerifiedLive");
        string Need(string? configured, string env, string name) =>
            Key(configured, env) is { Length: > 0 } k ? k : throw new ProviderExecutionException($"Provider Not Connected: set {env} (or Models:{name}ApiKey) to use {name}.", name);
        return Task.FromResult(provider switch
        {
            "ollama" or "local" => R("Ollama", opts.OllamaUrl, "qwen2.5:7b", ""),
            "anthropic" or "claude" => R("Anthropic", "", "claude-sonnet-4-5", Need(opts.AnthropicApiKey, "ANTHROPIC_API_KEY", "Anthropic")),
            // Gemini without a key uses Google Application Default Credentials (gcloud auth application-default login) via Vertex AI.
            "gemini" or "google" => R("Gemini", "", "gemini-2.5-flash", Key(opts.GeminiApiKey, "GEMINI_API_KEY") ?? (AdcHelper.HasAdc() ? "" : Need(opts.GeminiApiKey, "GEMINI_API_KEY", "Gemini"))),
            "openai" => R("OpenAI", "", "gpt-4.1-mini", Need(opts.OpenAiApiKey, "OPENAI_API_KEY", "OpenAI")),
            _ => throw new ProviderExecutionException($"Unknown model '{sel}'. Use ollama:<model>, anthropic:<model>, gemini:<model> or openai:<model>.")
        });
    }

    public const int MaxTokensCeiling = 16000;

    // A reply cut off at the token limit is retried once with twice the room; the usage of both calls is reported.
    public async Task<ChatResponse> CompleteAsync(ResolvedModel m, ChatRequest r, CancellationToken ct)
    {
        var p = providers.FirstOrDefault(x => x.Supports(m.Provider)) ?? throw new ProviderExecutionException($"Provider Error: no adapter for {ProviderErrors.Display(m.Provider)}.", m.Provider);
        var res = await p.CompleteAsync(m, r, ct);
        if (!res.Truncated || r.MaxTokens >= MaxTokensCeiling) return res;
        var again = await p.CompleteAsync(m, r with { MaxTokens = Math.Min(r.MaxTokens * 2, MaxTokensCeiling) }, ct);
        return again with { TokensIn = res.TokensIn + again.TokensIn, TokensOut = res.TokensOut + again.TokensOut };
    }
}
