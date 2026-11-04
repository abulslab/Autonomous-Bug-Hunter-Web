using BugHunter.Api.Projects;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using BugHunter.Api.Common;
using BugHunter.Api.Hunter;
using BugHunter.Api.Models;
using BugHunter.Api.Storage;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;
var env = builder.Environment;

// Models: keys from configuration (Models:AnthropicApiKey, ...) or the usual environment variables (ANTHROPIC_API_KEY,
// GEMINI_API_KEY, OPENAI_API_KEY); Gemini also works with Google Application Default Credentials.
builder.Services.AddSingleton(cfg.GetSection("Models").Get<ModelOptions>() ?? new());
builder.Services.AddHttpClient();
builder.Services.AddScoped<IModelProvider>(sp => new OpenAiCompatibleProvider(sp.GetRequiredService<IHttpClientFactory>().CreateClient()));
builder.Services.AddScoped<IModelProvider>(sp => new AnthropicProvider(sp.GetRequiredService<IHttpClientFactory>().CreateClient()));
builder.Services.AddScoped(sp => new OllamaProvider(sp.GetRequiredService<IHttpClientFactory>().CreateClient()) { DefaultEndpoint = sp.GetRequiredService<ModelOptions>().OllamaUrl, ContextTokens = sp.GetRequiredService<ModelOptions>().OllamaContextTokens });
builder.Services.AddScoped<IModelProvider>(sp => sp.GetRequiredService<OllamaProvider>());
builder.Services.AddScoped<ModelGateway>();

// Storage: JSON documents in local files (default, single node) or PostgreSQL (shared by several API instances).
var dataDir = Path.GetFullPath(cfg["Storage:DataDir"] is { Length: > 0 } d ? d : Path.Combine(env.ContentRootPath, "..", "..", "data"));
IDocStore store = cfg["Storage:Provider"]?.Equals("Postgres", StringComparison.OrdinalIgnoreCase) == true
    ? new PostgresDocStore(cfg.GetConnectionString("BugHunter") ?? throw new InvalidOperationException("Storage:Provider is Postgres but ConnectionStrings:BugHunter is not set."))
    : new FileDocStore(dataDir);
builder.Services.AddSingleton(store);

var hunter = cfg.GetSection("Hunter").Get<HunterOptions>() ?? new();
hunter.AllowPrivateTargets ??= !env.IsProduction();
hunter.AutoStartWorker ??= !env.IsProduction();
if (string.IsNullOrEmpty(hunter.MediaDir)) hunter.MediaDir = Path.Combine(dataDir, "media");
hunter.DemoShopKey ??= Environment.GetEnvironmentVariable("DEMO_SHOP_KEY");
hunter.SkillsDir = SkillPacks.FindDir(hunter.SkillsDir, env.ContentRootPath);
hunter.CustomSkillsDir ??= Path.Combine(dataDir, "skills");
builder.Services.AddSingleton(hunter);
builder.Services.AddHttpClient<BrowserWorkerClient>(c => c.Timeout = TimeSpan.FromSeconds(90));
builder.Services.AddScoped<DemoShopClient>();
builder.Services.AddSingleton(cfg.GetSection("GitHub").Get<GitHubOptions>() ?? new());
builder.Services.AddScoped<GitHubIssues>();
// Projects: applications under test with environments, roles and versioned knowledge (stories and documents).
var simulators = cfg.GetSection("Simulators").Get<SimulatorOptions>() ?? new();
builder.Services.AddSingleton(simulators);
builder.Services.AddScoped<ProjectStore>();
builder.Services.AddScoped<KnowledgeSources>();
builder.Services.AddScoped<BugHunter.Api.Review.ReviewService>();
builder.Services.AddScoped<KnowledgeService>();
builder.Services.AddScoped<HuntStore>();
builder.Services.AddScoped<HuntEngine>();
builder.Services.AddSingleton<HuntRunner>();
builder.Services.AddSingleton<BrowserWorkerLauncher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<HuntRunner>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<BrowserWorkerLauncher>());

// Observability: traces and metrics over OTLP when OTEL_EXPORTER_OTLP_ENDPOINT is set.
var otlp = !string.IsNullOrEmpty(cfg["OTEL_EXPORTER_OTLP_ENDPOINT"]);
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("bughunter-api"))
    .WithTracing(t => { t.AddSource(Telemetry.Name).AddAspNetCoreInstrumentation().AddHttpClientInstrumentation(); if (otlp) t.AddOtlpExporter(); })
    .WithMetrics(m => { m.AddMeter(Telemetry.Name).AddAspNetCoreInstrumentation(); if (otlp) m.AddOtlpExporter(); });

// Starting a hunt drives a real browser and may spend AI budget: limit how often it can be asked for.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddFixedWindowLimiter("hunts", w => { w.PermitLimit = cfg.GetValue("RateLimits:HuntsPerMinute", 10); w.Window = TimeSpan.FromMinutes(1); w.QueueLimit = 0; });
});

builder.Services.AddOpenApi(o => o.AddDocumentTransformer((doc, _, _) =>
{
    doc.Info.Title = "Autonomous Bug Hunter API";
    doc.Info.Description = "Start hunts, follow them live, and read findings with evidence, replay results and Playwright specs.";
    return Task.CompletedTask;
}));

var origins = cfg.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:3100"];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
if (store is PostgresDocStore pg) await pg.InitAsync(CancellationToken.None);
app.UseCors();
app.UseRateLimiter();

// Optional API key (Auth:ApiKey or BUGHUNTER_API_KEY). Media links carry their own short-lived tickets instead.
var apiKey = cfg["Auth:ApiKey"] is { Length: > 0 } k ? k : Environment.GetEnvironmentVariable("BUGHUNTER_API_KEY");
if (!string.IsNullOrEmpty(apiKey))
    app.Use(async (http, next) =>
    {
        var path = http.Request.Path;
        var open = !path.StartsWithSegments("/api") || path.StartsWithSegments("/api/hunter/media") || HttpMethods.IsOptions(http.Request.Method);
        var sent = http.Request.Headers["X-Api-Key"].FirstOrDefault() ?? http.Request.Headers.Authorization.ToString().Replace("Bearer ", "");
        if (!open && !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(sent), System.Text.Encoding.UTF8.GetBytes(apiKey)))
        { http.Response.StatusCode = 401; return; }
        await next();
    });

app.MapOpenApi();                                                     // /openapi/v1.json
app.MapScalarApiReference("/docs", o => o.WithTitle("Autonomous Bug Hunter API")); // interactive reference

// Liveness: the process answers. Readiness: storage and the browser worker are usable.
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" })).ExcludeFromDescription();
app.MapGet("/readyz", async (IDocStore docs, BrowserWorkerClient worker, CancellationToken ct) =>
{
    var (workerOk, workerError, _) = await worker.HealthAsync(ct);
    string? storageError = null;
    try { await docs.PutAsync("system", "probe", "ready", new System.Text.Json.Nodes.JsonObject { ["at"] = DateTime.UtcNow }, ct); }
    catch (Exception e) { storageError = e.Message; }
    var ready = workerOk && storageError is null;
    return Results.Json(new { status = ready ? "ready" : "not ready", storage = new { ok = storageError is null, backend = docs.Describe, error = storageError }, worker = new { ok = workerOk, error = workerError } },
        statusCode: ready ? 200 : 503);
}).ExcludeFromDescription();

var apiGroup = app.MapGroup("/api");
apiGroup.MapHunter();
apiGroup.MapSkills();
apiGroup.MapProjects();

// The demo apps as ready-made projects (development, or wherever their URLs are configured).
using (var scope = app.Services.CreateScope())
    await DemoProjects.EnsureAsync(scope.ServiceProvider.GetRequiredService<ProjectStore>(), hunter, simulators, DemoProjects.FindGroundTruth(env.ContentRootPath), CancellationToken.None);
app.Run();

public partial class Program;
