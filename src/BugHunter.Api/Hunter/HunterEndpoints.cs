using System.Text.RegularExpressions;
using BugHunter.Api.Common;
using BugHunter.Api.Models;

namespace BugHunter.Api.Hunter;

public record StoryDto(string Key, string? Title, string? Criteria, int? Version = null, string? Source = null);

public record HuntCreateDto(string Url, string? Mission, int? MaxSteps, string? Connection, Dictionary<string, string>? Secrets, bool? AllowIrreversible, string? Strategy = null,
    string[]? Requirements = null, string? Rules = null, bool? Probe = null, bool? Confirm = null, string? SetupUrl = null, string? SetupSecret = null,
    StoryDto[]? Stories = null, string? PlannerConnection = null, double? MaxCostUsd = null, bool? Shrink = null, string? Grounding = null,
    string? ProjectId = null, int? KnowledgeVersion = null, string? Environment = null, string? Role = null, string[]? SkillPacks = null, string? AfterHunt = null, bool? DynamicUsers = null);

public record HuntConfirmDto(Dictionary<string, string>? Secrets);
public record ModelCheckDto(string Connection);
public record ModelConfigDto(string? GeminiApiKey = null, string? AnthropicApiKey = null, string? OpenAiApiKey = null, string? OllamaUrl = null);

public static class HunterEndpoints
{
    static bool TestHost(string host) => host is "localhost" or "127.0.0.1" or "[::1]" || host.EndsWith(".localhost") || host.EndsWith(".test");
    static IResult Fail(string message, int status = 400) => Results.Problem(message, statusCode: status);
    static string DemoApp(string? app) => app?.Trim().ToLowerInvariant() == DemoShopClient.Hotel ? DemoShopClient.Hotel : DemoShopClient.Shop;

    public static void MapHunter(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/hunter").WithTags("Hunter");

        g.MapGet("/status", async (BrowserWorkerClient worker, BrowserWorkerLauncher launcher, HunterOptions opts, DemoShopClient demo, ModelGateway models, OllamaProvider ollama, GitHubIssues github, CancellationToken ct) =>
        {
            var (ok, error, sessions) = await worker.HealthAsync(ct);
            if (!ok && launcher.Enabled) _ = launcher.Kick(); // opening the page starts a local worker
            List<DemoDefect>? defects = null;
            if (demo.Url is not null) { try { defects = await demo.DefectsAsync(ct); } catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { /* demo shop not running */ } }
            var demos = new List<object>();
            foreach (var app in demo.Configured)
            {
                List<DemoDefect>? list = null;
                try { list = app == DemoShopClient.Shop ? defects : await demo.DefectsAsync(ct, app); } catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { /* not running */ }
                demos.Add(new { app, name = DemoShopClient.Names[app], enabled = list is not null, url = demo.UrlOf(app), resetUrl = demo.ResetUrlOf(app), defects = list });
            }
            var health = await ollama.CheckHealthAsync(ct: ct);
            return Results.Ok(new
            {
                worker = new { ok, error, sessions, url = opts.WorkerUrl, autoStart = launcher.Enabled, state = ok ? null : launcher.State },
                storefront = new { enabled = defects is not null, url = demo.Url, resetUrl = demo.ResetUrl },
                defects,
                demos,
                models = models.Configured().Select(m => new { id = m.Id, label = m.Label }), providers = models.ProviderStatus(),
                defaults = new { driver = models.DefaultSelection, planner = models.PlannerSelection(null), maxCostUsd = models.DefaultBudget },
                github = new { enabled = github.Enabled, repo = github.Repo },
                ollama = health.IsAvailable ? new { available = true, models = health.Models } : new { available = false, models = new List<string>() }
            });
        }).WithSummary("Worker, demo shop, models and defaults");

        // One small structured call: proves the key or credentials, the model name and JSON output, and shows the latency.
        g.MapPost("/models/config", async (ModelConfigDto d, ModelOptions modelOpts, HunterOptions hunterOpts, ModelGateway models, HttpContext http, IConfiguration cfg) =>
        {
            // Without an API key the API is open, so provider keys may be set only from this machine.
            var keyed = !string.IsNullOrEmpty(cfg["Auth:ApiKey"]) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BUGHUNTER_API_KEY"));
            if (!keyed && http.Connection.RemoteIpAddress is { } ip && !System.Net.IPAddress.IsLoopback(ip))
                return Results.Problem("Provider keys can be set remotely only when the API has an API key (BUGHUNTER_API_KEY). Otherwise set them as environment variables.", statusCode: 403);
            if (d.GeminiApiKey is not null) modelOpts.GeminiApiKey = string.IsNullOrWhiteSpace(d.GeminiApiKey) ? null : d.GeminiApiKey.Trim();
            if (d.AnthropicApiKey is not null) modelOpts.AnthropicApiKey = string.IsNullOrWhiteSpace(d.AnthropicApiKey) ? null : d.AnthropicApiKey.Trim();
            if (d.OpenAiApiKey is not null) modelOpts.OpenAiApiKey = string.IsNullOrWhiteSpace(d.OpenAiApiKey) ? null : d.OpenAiApiKey.Trim();
            if (!string.IsNullOrWhiteSpace(d.OllamaUrl))
            {
                if (await UrlGuard.CheckAsync(d.OllamaUrl.Trim(), hunterOpts.AllowPrivateTargets == true, allowHttp: true) is { } err) return Fail(err);
                modelOpts.OllamaUrl = d.OllamaUrl.Trim();
            }
            return Results.Ok(new { ok = true, providers = models.ProviderStatus(), models = models.Configured().Select(m => new { id = m.Id, label = m.Label }) });
        }).WithSummary("Configure provider API keys in memory for the running instance");
        g.MapPost("/models/check", async (ModelCheckDto d, ModelGateway models, CancellationToken ct) =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var m = await models.ResolveAsync(d.Connection, ct);
                var schema = new System.Text.Json.Nodes.JsonObject { ["type"] = "object", ["properties"] = new System.Text.Json.Nodes.JsonObject { ["ok"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "boolean" } }, ["required"] = new System.Text.Json.Nodes.JsonArray("ok") };
                using var call = CancellationTokenSource.CreateLinkedTokenSource(ct); call.CancelAfter(TimeSpan.FromSeconds(60));
                var res = await models.CompleteAsync(m, new ChatRequest("Reply with a JSON object.", "Reply {\"ok\": true}.", 0, 64, [], "connection-check", null, schema), call.Token);
                var json = BugHunter.Api.Contracts.OutputContracts.Parse(res.Text).Value is not null;
                return Results.Ok(new { ok = json, provider = m.Provider, model = m.Model, ms = sw.ElapsedMilliseconds, tokensIn = res.TokensIn, tokensOut = res.TokensOut, error = json ? null : "The reply was not JSON.", reply = res.Text.Length > 200 ? res.Text[..200] : res.Text });
            }
            catch (Exception e) when (e is ProviderExecutionException or HttpRequestException or OperationCanceledException or InvalidOperationException)
            {
                return Results.Ok(new { ok = false, provider = (string?)null, model = d.Connection, ms = sw.ElapsedMilliseconds, tokensIn = 0, tokensOut = 0, error = e is OperationCanceledException ? "No reply within 60 seconds." : e.Message });
            }
        }).WithSummary("Check that a model selection works");

        // ?app=shop (default) or ?app=hotel picks the demo app.
        g.MapGet("/demo/stories", async (DemoShopClient demo, string? app, CancellationToken ct) =>
        {
            app = DemoApp(app);
            try { return Results.Ok(await demo.StoriesAsync(ct, app)); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return Fail($"The {DemoShopClient.Names[app]} is not running.", 503); }
        });
        g.MapPut("/demo/defects", async (Dictionary<string, bool> flags, DemoShopClient demo, string? app, CancellationToken ct) =>
        {
            app = DemoApp(app);
            try { return Results.Ok(await demo.SetDefectsAsync(flags, ct, app)); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return Fail($"The {DemoShopClient.Names[app]} is not running.", 503); }
        });

        g.MapPost("/hunts", StartHunt).RequireRateLimiting("hunts").WithSummary("Start a hunt").WithDescription("Validates the target, stories, rules and setup hook, then queues the hunt. Secrets are kept in memory for the run only.");

        g.MapGet("/hunts", async (HuntStore store, CancellationToken ct) => Results.Ok((await store.ListAsync(ct)).Select(h => new
        {
            h.Id, h.Url, h.Mission, h.Status, h.Strategy, h.Policy, h.Model, h.PlannerModel, h.CreatedBy, h.CreatedAt, h.StartedAt, h.FinishedAt, h.MaxSteps, steps = h.Steps.Count(s => s.N > 0),
            confirmed = h.Findings.Count(f => f.Reproduced == "Confirmed"), costUsd = h.Usage.CostUsd, h.Usage.CostPerConfirmedBug, h.Usage.ModelCalls,
            intents = h.Intents.Count, intentsHeld = h.Intents.Count(i => i.Status == "Held"), intentsViolated = h.Intents.Count(i => i.Status == "Violated"),
            observed = h.Findings.Count(f => f.Provenance == "Observed"), judged = h.Findings.Count(f => f.Provenance == "Judged"),
            rules = h.Invariants.Count, broken = h.Findings.Count(f => f.Provenance == "Invariant"),
            high = h.Findings.Count(f => f.Provenance == "Observed" && f.Severity is "High" or "Critical"),
            benchmark = h.Benchmark is { } b ? new { b.Target, b.Detected, b.Total, b.ConfirmedDetected, b.UnmatchedObserved, b.InvariantFindings, b.UnmatchedInvariant, b.UnmatchedInvariantFlagged, b.PredictionFindings, b.UnmatchedPrediction } : null
        })));

        // App maps learned across hunts, one per site: states, roads, the observed API and the last warm-up's intents.
        g.MapGet("/maps", async (HuntStore store, CancellationToken ct) => Results.Ok((await store.MapsAsync(ct)).Select(m => new
        {
            m.Site, m.Hunts, m.UpdatedAt, states = m.States.Count, roads = m.Roads.Count, apis = m.Apis.Count, intents = m.Intents.Count, domain = m.Model?.Domain
        }))).WithSummary("Sites with an app map");
        g.MapGet("/maps/detail", async (string site, HuntStore store, CancellationToken ct) =>
            await store.MapAsync(site, ct) is { } m ? Results.Ok(m) : Results.NotFound()).WithSummary("One site's app map");

        g.MapGet("/hunts/{id}", async (string id, HuntStore store, HuntRunner runner, CancellationToken ct) =>
            await store.GetAsync(id, ct) is { } h ? Results.Ok(new { hunt = h, active = runner.IsActive(id) }) : Results.NotFound());

        // The hunt as a document (md, html) or for pipelines (sarif, junit).
        g.MapGet("/hunts/{id}/report", async (string id, string? format, HuntStore store, CancellationToken ct) =>
        {
            if (await store.GetAsync(id, ct) is not { } h) return Results.NotFound();
            return (format ?? "md").ToLowerInvariant() switch
            {
                "md" or "markdown" => Results.Text(Reports.Markdown(h), "text/markdown; charset=utf-8"),
                "html" => Results.Text(Reports.Html(h), "text/html; charset=utf-8"),
                "sarif" => Results.Text(Reports.Sarif(h).ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), "application/sarif+json"),
                "junit" => Results.Text(Reports.JUnit(h), "application/xml; charset=utf-8"),
                _ => Fail("format must be md, html, sarif or junit.")
            };
        }).WithSummary("Export a hunt: Markdown, HTML, SARIF or JUnit");

        // File one finding as a GitHub issue (or comment on the open issue for the same bug).
        g.MapPost("/hunts/{id}/findings/{fid}/issue", async (string id, string fid, HuntStore store, GitHubIssues github, CancellationToken ct) =>
        {
            if (await store.GetAsync(id, ct) is not { } h || h.Findings.FirstOrDefault(f => f.Id == fid) is not { } f) return Results.NotFound();
            if (!github.Enabled) return Fail("GitHub filing is not configured: set GITHUB_TOKEN and GitHub:Repo.", 409);
            try
            {
                var (url, created) = await github.FileAsync(h, f, ct);
                f.IssueUrl = url; await store.SaveAsync(h, ct);
                return Results.Ok(new { url, created });
            }
            catch (Exception e) when (e is InvalidOperationException or HttpRequestException) { return Fail(e.Message, 502); }
        }).WithSummary("File a finding as a GitHub issue, without duplicates");

        g.MapGet("/hunts/{id}/shots/{n:int}", async (string id, int n, HuntStore store, CancellationToken ct) =>
            await store.ShotAsync(id, n, ct) is { } bytes ? Results.File(bytes, "image/jpeg") : Results.NotFound());

        // <img> and <video> cannot send an API key, so they present a short-lived ticket instead.
        g.MapPost("/hunts/{id}/ticket", async (string id, HuntStore store, HuntRunner runner, CancellationToken ct) =>
        {
            if (await store.GetAsync(id, ct) is not { } h) return Results.NotFound();
            var t = runner.Ticket("local", id);
            return Results.Ok(new { live = runner.Live.ContainsKey(id) ? $"/api/hunter/media/{t}/live" : null, video = h.HasVideo ? $"/api/hunter/media/{t}/video" : null });
        });

        // Ticket-authorised media: the live MJPEG view of a running hunt and the recording of a finished one.
        var media = api.MapGroup("/hunter/media/{ticket}");
        media.MapGet("/live", async (string ticket, HttpContext http, HuntRunner runner, BrowserWorkerClient worker) =>
        {
            if (runner.Redeem(ticket) is not { } t || !runner.Live.TryGetValue(t.Hunt, out var session)) { http.Response.StatusCode = 404; return; }
            using var res = await worker.StreamAsync(session, http.RequestAborted);
            http.Response.ContentType = res.Content.Headers.ContentType?.ToString() ?? "multipart/x-mixed-replace; boundary=frame";
            http.Response.Headers.CacheControl = "no-store";
            await using var src = await res.Content.ReadAsStreamAsync(http.RequestAborted);
            var buf = new byte[64 * 1024];
            try
            {
                int n;
                while ((n = await src.ReadAsync(buf, http.RequestAborted)) > 0) { await http.Response.Body.WriteAsync(buf.AsMemory(0, n), http.RequestAborted); await http.Response.Body.FlushAsync(http.RequestAborted); }
            }
            catch (OperationCanceledException) { /* viewer closed the page */ }
            catch (IOException) { /* the hunt ended and the worker closed the stream */ }
        });
        media.MapGet("/video", (string ticket, HuntRunner runner, HunterOptions opts) =>
            runner.Redeem(ticket) is { } t && HuntRunner.VideoPath(opts, t.Hunt) is var file && File.Exists(file)
                ? Results.File(file, "video/webm", enableRangeProcessing: true) : Results.NotFound());

        // Replays a finished hunt's findings again (for example after the app was fixed, or to re-check flaky ones).
        g.MapPost("/hunts/{id}/confirm", async (string id, HuntConfirmDto d, HuntStore store, HuntRunner runner, BrowserWorkerLauncher launcher, HunterOptions opts, CancellationToken ct) =>
        {
            if (await store.GetAsync(id, ct) is not { } h) return Results.NotFound();
            if (h.Status is "Queued" or "Running" or "Confirming" || runner.IsActive(id)) return Fail("The hunt is still running.", 409);
            var secrets = (d.Secrets ?? []).Where(kv => !string.IsNullOrEmpty(kv.Value)).ToDictionary(kv => kv.Key.Trim(), kv => kv.Value);
            if (h.SecretNames.Except(secrets.Keys).ToList() is { Count: > 0 } missing) return Fail($"Enter the secrets this hunt used: {string.Join(", ", missing)}.");
            if (!await launcher.EnsureRunningAsync(TimeSpan.FromSeconds(90), ct))
                return Fail(launcher.State ?? $"The browser worker at {opts.WorkerUrl} is not reachable.", 503);
            runner.Enqueue(new(h.Id, secrets, ConfirmOnly: true));
            return Results.Accepted();
        }).RequireRateLimiting("hunts").WithSummary("Replay a finished hunt's findings again");

        g.MapPost("/hunts/{id}/cancel", async (string id, HuntStore store, HuntRunner runner, CancellationToken ct) =>
        {
            if (await store.GetAsync(id, ct) is not { } h) return Results.NotFound();
            if (h.Status is not ("Queued" or "Running" or "Confirming")) return Fail($"The hunt is already {h.Status.ToLowerInvariant()}.", 409);
            runner.Cancel(id);
            return Results.Accepted();
        }).WithSummary("Stop a running hunt");
    }


    // Validates and queues a hunt. Used by POST /hunts and by project hunts, which build the request from the project.
    internal static async Task<IResult> StartHunt(HuntCreateDto d, HuntStore store, HuntRunner runner, BrowserWorkerLauncher launcher, HunterOptions opts, DemoShopClient demo, ModelGateway models, CancellationToken ct) =>
        (await StartHuntCore(d, store, runner, launcher, opts, demo, models, ct)).Result;

    // The hunt's id when it was queued, and the HTTP result either way.
    internal static async Task<(string? Id, IResult Result)> StartHuntCore(HuntCreateDto d, HuntStore store, HuntRunner runner, BrowserWorkerLauncher launcher, HunterOptions opts, DemoShopClient demo, ModelGateway models, CancellationToken ct)
    {
            if (!Uri.TryCreate(d.Url?.Trim(), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return (null, Fail("Enter an absolute http(s) URL to hunt on."));
            if (await UrlGuard.CheckAsync(u.ToString(), opts.AllowPrivateTargets == true, allowHttp: true) is { } blocked) return (null, Fail(blocked));
            var secrets = (d.Secrets ?? []).Where(kv => !string.IsNullOrEmpty(kv.Value)).ToDictionary(kv => kv.Key.Trim(), kv => kv.Value);
            if (secrets.Count > 10 || secrets.Any(kv => !Regex.IsMatch(kv.Key, @"^[\w.-]{1,40}$") || kv.Value.Length > 200))
                return (null, Fail("Up to 10 secrets; names use letters, digits, '.', '_' or '-' (max 40) and values are at most 200 characters."));
            if ((d.Mission ?? "").Length > 2000) return (null, Fail("The mission is limited to 2000 characters."));
            if (d.MaxCostUsd is < 0 or > 50) return (null, Fail("The AI budget must be between $0 and $50 per hunt."));
            if ((d.Grounding ?? "").Length > 8000) return (null, Fail("Product notes are limited to 8000 characters."));
            var strategy = d.Connection == "heuristic" ? "Heuristic" : d.Strategy ?? "Hybrid";
            if (strategy is not ("Guided" or "Hybrid" or "Model" or "Heuristic")) return (null, Fail("Strategy must be Guided, Hybrid, Model or Heuristic."));
            if ((d.Rules ?? "").Length > 8000) return (null, Fail("Rules are limited to 8000 characters."));
            var (rules, ruleErrors) = InvariantRules.ParseLines(d.Rules);
            if (ruleErrors.Count > 0) return (null, Fail($"Rule {string.Join("; ", ruleErrors.Take(3))}"));
            if (rules.Count > 30) return (null, Fail("Up to 30 rules."));
            // The setup hook may only call the target's own host, with a secret the hunt was given.
            string? setupUrl = null;
            if (!string.IsNullOrWhiteSpace(d.SetupUrl))
            {
                if (!Uri.TryCreate(new Uri(u, "/"), d.SetupUrl.Trim(), out var su) || su.Scheme is not ("http" or "https") || su.Host != u.Host)
                    return (null, Fail("The setup URL must be on the same host as the target."));
                setupUrl = su.ToString();
            }
            // A built-in demo app's reset key is held by the server: a hunt on a demo app that names a setup secret without a
            // value gets the key here, so it never has to be sent to a browser or a script.
            if (d.SetupSecret is { Length: > 0 } named && !secrets.ContainsKey(named) && demo.AppFor(u.ToString()) is not null && demo.AdminKey is { Length: > 0 } demoKey)
                secrets[named] = demoKey;
            if (d.SetupSecret is { Length: > 0 } ss && !secrets.ContainsKey(ss)) return (null, Fail($"The setup secret '{ss}' is not one of this hunt's secrets."));

            // Stories: given in full, or by key from the demo shop's backlog.
            var stories = (d.Stories ?? []).Where(s => !string.IsNullOrWhiteSpace(s.Key))
                .Select(s => new HuntRequirement(s.Key.Trim().ToUpperInvariant(), s.Title?.Trim() ?? "", Clip(s.Criteria ?? ""), s.Source ?? "Provided", s.Version)).ToList();
            var keys = (d.Requirements ?? []).SelectMany(k => k.Split(',', ' ', ';')).Select(k => k.Trim().ToUpperInvariant()).Where(k => k.Length > 0).Distinct().ToList();
            if (keys.Count + stories.Count > 10) return (null, Fail("Up to 10 requirements per hunt."));
            if (keys.Count > 0)
            {
                List<DemoStory> backlog;
                var app = demo.AppFor(u.ToString()) ?? DemoShopClient.Shop;
                try { backlog = await demo.StoriesAsync(ct, app); }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return (null, Fail($"Requirement keys are looked up in the {DemoShopClient.Names[app]}, which is not running. Pass the stories in full instead.")); }
                foreach (var key in keys)
                {
                    if (backlog.FirstOrDefault(s => s.Key == key) is not { } s) return (null, Fail($"Requirement {key} was not found."));
                    stories.Add(new(s.Key, s.Title, Clip(s.Criteria), DemoShopClient.Names[app]));
                }
            }
            var h = new Hunt
            {
                Id = "H-" + Guid.NewGuid().ToString("N")[..10], Url = u.ToString(), Mission = d.Mission?.Trim() ?? "",
                MaxSteps = Math.Clamp(d.MaxSteps ?? 40, 5, 150), Strategy = strategy,
                Connection = string.IsNullOrWhiteSpace(d.Connection) || d.Connection == "heuristic" ? null : d.Connection.Trim(),
                AllowIrreversible = d.AllowIrreversible ?? TestHost(u.Host.ToLowerInvariant()), SecretNames = [.. secrets.Keys], CreatedBy = Environment.GetEnvironmentVariable("BUGHUNTER_USER") ?? "operator",
                Requirements = stories, Invariants = rules, Probe = d.Probe ?? true, Confirm = d.Confirm ?? true, SetupUrl = setupUrl,
                SetupSecret = setupUrl is null || string.IsNullOrWhiteSpace(d.SetupSecret) ? null : d.SetupSecret,
                DynamicUsers = d.DynamicUsers ?? false, // opt-in: it rewrites test e-mails as plus-addresses, which only works where such accounts can exist
                PlannerConnection = string.IsNullOrWhiteSpace(d.PlannerConnection) ? null : d.PlannerConnection.Trim(),
                MaxCostUsd = d.MaxCostUsd ?? models.DefaultBudget, Shrink = d.Shrink ?? true, Grounding = string.IsNullOrWhiteSpace(d.Grounding) ? null : d.Grounding.Trim(),
                ProjectId = d.ProjectId, KnowledgeVersion = d.KnowledgeVersion, Environment = d.Environment, Role = d.Role, ForcedSkillPacks = d.SkillPacks?.ToList() ?? []
            };
            if (!await launcher.EnsureRunningAsync(TimeSpan.FromSeconds(90), ct))
                return (null, Fail(launcher.State ?? $"The browser worker at {opts.WorkerUrl} is not reachable.", 503));
            await store.SaveAsync(h, ct);
            runner.Enqueue(new(h.Id, secrets, After: d.AfterHunt));
            return (h.Id, Results.Accepted($"/api/hunter/hunts/{h.Id}", new { id = h.Id }));
    }

    static string Clip(string s) => s.Length > 1500 ? s[..1500] : s;
}
