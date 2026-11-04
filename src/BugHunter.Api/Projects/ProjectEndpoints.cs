using System.Text.RegularExpressions;
using BugHunter.Api.Hunter;
using BugHunter.Api.Models;

namespace BugHunter.Api.Projects;

public record ProjectDto(string? Id, string? Name, string? Mission, List<ProjectEnvironment>? Environments, List<ProjectRole>? Roles, KnowledgeConfig? Knowledge,
    List<string>? SkillPacks, List<string>? OutOfScope, List<SpecSource>? Specs = null);
public record SurveyDto(string? Environment, string[]? Roles, int? MaxSteps, bool? Confirm = null);
public record UnderstandDto(string? Model, bool? Force);
public record ProjectHuntDto(string? Environment, string? Role, string? Strategy, string? Connection, string? PlannerConnection, int? MaxSteps, double? MaxCostUsd,
    string[]? Stories, bool? Probe, bool? Confirm, bool? Shrink, string? Mission, bool? Sync);

public class KnowledgeService(ProjectStore store, KnowledgeSources sources)
{
    // Pulls stories and documents, and saves a new snapshot only if something changed.
    public async Task<(KnowledgeSnapshot Snapshot, bool Changed)> SyncAsync(Project p, CancellationToken ct)
    {
        var (storySource, docSource) = await sources.ForAsync(p, ct);
        var stories = storySource is null ? [] : await storySource.StoriesAsync(ct);
        var docs = docSource is null ? [] : await docSource.DocumentsAsync(ct);
        var prev = p.KnowledgeVersion > 0 ? await store.KnowledgeAsync(p.Id, p.KnowledgeVersion, ct) : null;
        var next = KnowledgeDiffer.Next(p.Id, prev, stories, docs, storySource?.Name ?? "none", docSource?.Name ?? "none");
        if (prev is not null && next.Diff.Empty) return (prev, false);
        await store.SaveKnowledgeAsync(next, ct);
        p.KnowledgeVersion = next.Version;
        if (prev is not null) await ApplyChangeAsync(p, next, ct);
        await store.SaveAsync(p, ct);
        return (next, true);
    }

    // A story change: traceability finds what it touches; rules of changed stories go stale and affected journeys are
    // proven again. The impact waits for the next change-driven hunt.
    async Task ApplyChangeAsync(Project p, KnowledgeSnapshot k, CancellationToken ct)
    {
        var twin = p.TwinVersion > 0 ? await store.TwinAsync(p.Id, p.TwinVersion, ct) : null;
        var journeys = await store.JourneysAsync(p.Id, ct);
        var impact = Learning.ChangeAnalysis.ForStories(k, twin, journeys);
        if (impact.Stories.Count == 0) return;
        Learning.ChangeAnalysis.Apply(impact, twin, journeys);
        if (twin is not null) await store.SaveTwinAsync(twin, ct);
        foreach (var j in journeys.Where(j => impact.Journeys.Contains(j.Id))) await store.SaveJourneyAsync(j, ct);
        if (p.PendingChange is { } before)
            foreach (var (a, b) in new[] { (impact.Stories, before.Stories), (impact.Features, before.Features), (impact.Endpoints, before.Endpoints), (impact.Rules, before.Rules), (impact.Journeys, before.Journeys) })
                a.AddRange(b.Except(a));
        p.PendingChange = impact;
    }
}

public static partial class ProjectEndpoints
{
    static IResult Fail(string message, int status = 400) => Results.Problem(message, statusCode: status);

    static object View(Project p, IEnumerable<string> secretNames, KnowledgeSnapshot? k) => new
    {
        p.Id, p.Name, p.Mission, p.Environments, p.Roles, p.Knowledge, p.SkillPacks, p.OutOfScope, p.Specs, p.KnowledgeVersion, p.TwinVersion, p.Workflow, p.BaselineRun, p.CreatedAt, p.UpdatedAt,
        secrets = secretNames.Order(),
        latestKnowledge = k is null ? null : new { k.Version, k.SyncedAt, k.StorySource, k.DocumentSource, stories = k.Stories.Count, documents = k.Documents.Count }
    };

    static string? GroundTruthDir => DemoProjects.FindGroundTruth(AppContext.BaseDirectory) ?? DemoProjects.FindGroundTruth(Directory.GetCurrentDirectory());

    static async Task<string?> ValidateAsync(ProjectDto d, HunterOptions opts)
    {
        if (string.IsNullOrWhiteSpace(d.Name)) return "A project needs a name.";
        if ((d.Environments ?? []).Count == 0) return "A project needs at least one environment.";
        var allowPrivate = opts.AllowPrivateTargets == true;
        foreach (var e in d.Environments!)
        {
            if (string.IsNullOrWhiteSpace(e.Name) || !Uri.TryCreate(e.Url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return $"Environment '{e.Name}' needs a name and an absolute http(s) URL.";
            if (await Common.UrlGuard.CheckAsync(u.ToString(), allowPrivate, allowHttp: true) is { } envErr) return envErr;
            if (!string.IsNullOrWhiteSpace(e.ResetUrl) && (!Uri.TryCreate(new Uri(u, "/"), e.ResetUrl.Trim(), out var ru) || ru.Scheme is not ("http" or "https") || ru.Host != u.Host))
                return "The reset URL must be on the same host as the environment.";
        }
        if ((d.Roles ?? []).Any(r => !IdPattern().IsMatch(r.Id ?? ""))) return "Role ids use lower-case letters, digits and '-'.";
        foreach (var c in new[] { d.Knowledge?.Stories, d.Knowledge?.Documents }.OfType<SourceConfig>())
        {
            if (c.Kind is not ("none" or "files" or "jira" or "confluence")) return $"Unknown knowledge source '{c.Kind}'.";
            if (c.Kind == "files" && Common.UrlGuard.CheckLocalPath(c.Path, GroundTruthDir) is { } pathErr) return pathErr;
            if (c.Kind is "jira" or "confluence")
            {
                if (string.IsNullOrWhiteSpace(c.BaseUrl) || string.IsNullOrWhiteSpace(c.Key)) return $"A {c.Kind} source needs a base URL and a key.";
                if (await Common.UrlGuard.CheckAsync(c.BaseUrl, allowPrivate, allowHttp: true) is { } urlErr) return urlErr;
            }
        }
        foreach (var s in d.Specs ?? [])
        {
            if (s.Url is { Length: > 0 } su && await Common.UrlGuard.CheckAsync(su, allowPrivate, allowHttp: true) is { } specUrlErr) return specUrlErr;
            if (s.Path is { Length: > 0 } sp && Common.UrlGuard.CheckLocalPath(sp, GroundTruthDir) is { } specPathErr) return specPathErr;
        }
        return null;
    }

    static string? Validate(ProjectDto d)
    {
        if (string.IsNullOrWhiteSpace(d.Name)) return "A project needs a name.";
        if ((d.Environments ?? []).Count == 0) return "A project needs at least one environment.";
        foreach (var e in d.Environments!)
            if (string.IsNullOrWhiteSpace(e.Name) || !Uri.TryCreate(e.Url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return $"Environment '{e.Name}' needs a name and an absolute http(s) URL.";
        if ((d.Roles ?? []).Any(r => !IdPattern().IsMatch(r.Id ?? ""))) return "Role ids use lower-case letters, digits and '-'.";
        foreach (var c in new[] { d.Knowledge?.Stories, d.Knowledge?.Documents }.OfType<SourceConfig>())
        {
            if (c.Kind is not ("none" or "files" or "jira" or "confluence")) return $"Unknown knowledge source '{c.Kind}'.";
            if (c.Kind == "files" && string.IsNullOrWhiteSpace(c.Path)) return "A files source needs a path.";
            if (c.Kind is "jira" or "confluence" && (string.IsNullOrWhiteSpace(c.BaseUrl) || string.IsNullOrWhiteSpace(c.Key))) return $"A {c.Kind} source needs a base URL and a key.";
        }
        return null;
    }

    static void Apply(Project p, ProjectDto d)
    {
        p.Name = d.Name!.Trim(); p.Mission = d.Mission?.Trim(); p.Environments = d.Environments!; p.Roles = d.Roles ?? [];
        p.Knowledge = d.Knowledge ?? new(); p.SkillPacks = d.SkillPacks ?? []; p.OutOfScope = d.OutOfScope ?? []; p.Specs = d.Specs ?? [];
    }

    public static void MapProjects(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/projects").WithTags("Projects");

        g.MapGet("", async (ProjectStore store, CancellationToken ct) =>
        {
            var list = new List<object>();
            foreach (var p in await store.ListAsync(ct))
                list.Add(View(p, (await store.SecretsAsync(p.Id, ct)).Keys, p.KnowledgeVersion > 0 ? await store.KnowledgeAsync(p.Id, p.KnowledgeVersion, ct) : null));
            return Results.Ok(list);
        }).WithSummary("Projects");

        g.MapGet("/{id}", async (string id, ProjectStore store, CancellationToken ct) =>
            await store.GetAsync(id, ct) is { } p ? Results.Ok(View(p, (await store.SecretsAsync(id, ct)).Keys, p.KnowledgeVersion > 0 ? await store.KnowledgeAsync(id, p.KnowledgeVersion, ct) : null)) : Results.NotFound())
            .WithSummary("A project, with the names (never the values) of its secrets");

        g.MapPost("", async (ProjectDto d, ProjectStore store, HunterOptions opts, CancellationToken ct) =>
        {
            if (!IdPattern().IsMatch(d.Id ?? "")) return Fail("The project id uses 2 to 40 lower-case letters, digits and '-'.");
            if (await ValidateAsync(d, opts) is { } err) return Fail(err);
            if (await store.GetAsync(d.Id!, ct) is not null) return Fail($"Project '{d.Id}' already exists.", 409);
            var p = new Project { Id = d.Id! };
            Apply(p, d);
            await store.SaveAsync(p, ct);
            return Results.Created($"/api/projects/{p.Id}", View(p, [], null));
        }).WithSummary("Create a project");

        g.MapPut("/{id}", async (string id, ProjectDto d, ProjectStore store, HunterOptions opts, CancellationToken ct) =>
        {
            if (await store.GetAsync(id, ct) is not { } p) return Results.NotFound();
            if (await ValidateAsync(d, opts) is { } err) return Fail(err);
            Apply(p, d);
            await store.SaveAsync(p, ct);
            return Results.Ok(View(p, (await store.SecretsAsync(id, ct)).Keys, null));
        }).WithSummary("Update a project");

        g.MapDelete("/{id}", async (string id, ProjectStore store, CancellationToken ct) => { await store.DeleteAsync(id, ct); return Results.NoContent(); }).WithSummary("Delete a project");

        g.MapPut("/{id}/secrets", async (string id, Dictionary<string, string?> values, ProjectStore store, CancellationToken ct) =>
        {
            if (await store.GetAsync(id, ct) is null) return Results.NotFound();
            if (values.Keys.Any(k => !Regex.IsMatch(k, @"^[\w.-]{1,40}$")) || values.Values.Any(v => (v ?? "").Length > 500)) return Fail("Secret names use letters, digits, '.', '_' or '-' (max 40); values are at most 500 characters.");
            await store.SetSecretsAsync(id, values, ct);
            return Results.Ok(new { secrets = (await store.SecretsAsync(id, ct)).Keys.Order() });
        }).WithSummary("Set or clear project secrets (write-only)").WithDescription("An empty value removes the secret. Values are never returned.");

        g.MapPost("/{id}/sync", async (string id, ProjectStore store, KnowledgeService knowledge, CancellationToken ct) =>
        {
            if (await store.GetAsync(id, ct) is not { } p) return Results.NotFound();
            try
            {
                var (k, changed) = await knowledge.SyncAsync(p, ct);
                return Results.Ok(new { k.Version, changed, k.Diff, stories = k.Stories.Count, documents = k.Documents.Count });
            }
            catch (Exception e) when (e is KnowledgeSourceException or HttpRequestException or IOException) { return Fail($"Knowledge sync failed: {e.Message}", 502); }
        }).WithSummary("Pull stories and documents; a new knowledge version only when something changed");

        g.MapGet("/{id}/knowledge", async (string id, int? version, ProjectStore store, CancellationToken ct) =>
        {
            if (await store.GetAsync(id, ct) is not { } p) return Results.NotFound();
            return await store.KnowledgeAsync(id, version ?? p.KnowledgeVersion, ct) is { } k ? Results.Ok(k) : Results.NotFound();
        }).WithSummary("A knowledge snapshot (latest by default)");

        g.MapGet("/{id}/knowledge/history", async (string id, ProjectStore store, CancellationToken ct) =>
            Results.Ok((await store.KnowledgeHistoryAsync(id, ct)).Select(k => new { k.Version, k.SyncedAt, k.StorySource, k.DocumentSource, k.Diff })))
            .WithSummary("Every knowledge version with what changed");

        g.MapGet("/{id}/twin", async (string id, int? version, ProjectStore store, CancellationToken ct) =>
        {
            if (await store.GetAsync(id, ct) is not { } p) return Results.NotFound();
            return await store.TwinAsync(id, version ?? p.TwinVersion, ct) is { } t ? Results.Ok(t) : Results.NotFound();
        }).WithSummary("The app twin (latest version by default)");

        g.MapGet("/{id}/twin/history", async (string id, ProjectStore store, CancellationToken ct) =>
            Results.Ok((await store.TwinHistoryAsync(id, ct)).Select(t => new { t.Version, t.BuiltAt, t.FromHunt, t.KnowledgeVersion, states = t.States.Count, endpoints = t.Endpoints.Count, t.Changes })))
            .WithSummary("Every twin version with what it added");

        g.MapGet("/{id}/hunts", async (string id, HuntStore hunts, CancellationToken ct) => Results.Ok((await hunts.ListAsync(ct)).Where(h => h.ProjectId == id).Select(h => new
        {
            h.Id, h.Status, h.Strategy, h.Environment, h.Role, h.KnowledgeVersion, h.CreatedAt, h.FinishedAt,
            confirmed = h.Findings.Count(f => f.Reproduced == "Confirmed"), costUsd = h.Usage.CostUsd
        }))).WithSummary("The project's hunts");

        // A hunt built from the project: the environment's URL and reset hook, a role's test account, the stories and
        // documents of the latest knowledge (synced first unless sync is false), the mission and skill packs.
        g.MapPost("/{id}/hunts", async (string id, ProjectHuntDto d, ProjectStore store, KnowledgeService knowledge, HuntStore hunts, HuntRunner runner,
            BrowserWorkerLauncher launcher, HunterOptions opts, DemoShopClient demo, ModelGateway models, CancellationToken ct) =>
        {
            if (await store.GetAsync(id, ct) is not { } p) return Results.NotFound();
            var (huntId, result) = await StartProjectHunt(p, d, null, store, knowledge, hunts, runner, launcher, opts, demo, models, ct);
            if (huntId is not null) await store.SaveRunAsync(new Runs.ProjectRun { Id = $"R-{huntId[2..]}", ProjectId = id, Kind = "hunt", Hunts = [huntId] }, ct);
            return result;
        }).RequireRateLimiting("hunts").WithSummary("Start a hunt for the project");

        // One no-AI hunt per role, one after another (each waits for the one before, so their test-data resets do not
        // collide): the twin then knows what every role can and cannot do.
        g.MapPost("/{id}/survey", async (string id, SurveyDto d, ProjectStore store, KnowledgeService knowledge, HuntStore hunts, HuntRunner runner,
            BrowserWorkerLauncher launcher, HunterOptions opts, DemoShopClient demo, ModelGateway models, CancellationToken ct) =>
        {
            if (await store.GetAsync(id, ct) is not { } p) return Results.NotFound();
            var roles = d.Roles is { Length: > 0 } wanted ? p.Roles.Where(r => wanted.Contains(r.Id)).ToList() : p.Roles;
            if (roles.Count == 0) return Fail("The project has no roles to survey.");
            var started = new List<object>(); var ids = new List<string>(); string? after = null;
            foreach (var r in roles)
            {
                var (huntId, result) = await StartProjectHunt(p, new ProjectHuntDto(d.Environment, r.Id, "Heuristic", "heuristic", null, d.MaxSteps ?? 60, null, null, true, d.Confirm ?? false, false, null, after is null ? null : false),
                    after, store, knowledge, hunts, runner, launcher, opts, demo, models, ct);
                if (huntId is null) return started.Count == 0 ? result : Results.Accepted(null, new { hunts = started, error = $"Stopped at role {r.Id}." });
                started.Add(new { role = r.Id, hunt = huntId }); ids.Add(huntId);
                after = huntId;
            }
            var run = new Runs.ProjectRun { Id = $"R-{after![2..]}", ProjectId = id, Kind = "survey", Hunts = ids };
            await store.SaveRunAsync(run, ct);
            return Results.Accepted(null, new { hunts = started, run = run.Id });
        }).RequireRateLimiting("hunts").WithSummary("Survey every role without AI");

        // Imports the project's specifications into the twin as declared endpoints (OpenAPI by URL or path), or a HAR
        // file sent in the body as recorded traffic.
        g.MapPost("/{id}/specs/import", async (string id, ProjectStore store, IHttpClientFactory http, HunterOptions opts, HttpRequest req, CancellationToken ct) =>
        {
            if (await store.GetAsync(id, ct) is not { } p) return Results.NotFound();
            var twin = p.TwinVersion > 0 ? await store.TwinAsync(id, p.TwinVersion, ct) : null;
            var imported = new List<object>();
            try
            {
                if (req.ContentLength > 0 && req.ContentType?.Contains("json") == true)
                {
                    var node = await System.Text.Json.Nodes.JsonNode.ParseAsync(req.Body, cancellationToken: ct);
                    if (node?["paths"] is not null || node?["openapi"] is not null || node?["swagger"] is not null)
                    {
                        var declared = Twin.Specs.OpenApi(node!);
                        twin = Twin.TwinBuilder.Declare(twin, p, declared, $"uploaded {req.Query["name"].FirstOrDefault() ?? "openapi.json"}");
                        imported.Add(new { kind = "openapi", source = req.Query["name"].FirstOrDefault() ?? "upload", endpoints = declared.Count });
                    }
                    else if (node?["log"]?["entries"] is not null)
                    {
                        var events = Twin.Specs.Har(node!);
                        twin = Twin.TwinBuilder.ObserveHar(twin, p, events, $"har {req.Query["name"].FirstOrDefault() ?? "upload"}");
                        imported.Add(new { kind = "har", calls = events.Count });
                    }
                    else return Fail("The JSON body was neither an OpenAPI document (missing 'paths') nor a HAR file (missing 'log.entries').");
                }
                else
                    foreach (var spec in p.Specs)
                    {
                        if (spec.Url is { Length: > 0 } su && await Common.UrlGuard.CheckAsync(su, opts.AllowPrivateTargets == true, allowHttp: true) is { } uErr) return Fail(uErr);
                        if (spec.Url is not { Length: > 0 } && Common.UrlGuard.CheckLocalPath(spec.Path, GroundTruthDir) is { } pErr) return Fail(pErr);
                        var text = spec.Url is { Length: > 0 } u ? await http.CreateClient().GetStringAsync(u, ct) : await File.ReadAllTextAsync(spec.Path!, ct);
                        var declared = Twin.Specs.OpenApi(System.Text.Json.Nodes.JsonNode.Parse(text)!);
                        twin = Twin.TwinBuilder.Declare(twin, p, declared, $"openapi {spec.Url ?? spec.Path}");
                        imported.Add(new { kind = "openapi", source = spec.Url ?? spec.Path, endpoints = declared.Count });
                    }
            }
            catch (Exception e) when (e is HttpRequestException or IOException or System.Text.Json.JsonException) { return Fail($"Import failed: {e.Message}", 502); }
            if (imported.Count == 0) return Fail("The project has no specifications; add an openapi source or send a HAR file.");
            await store.SaveTwinAsync(twin!, ct);
            p.TwinVersion = twin!.Version;
            await store.SaveAsync(p, ct);
            return Results.Ok(new { twinVersion = twin.Version, imported, twin.Changes, unknowns = twin.Unknowns.Count });
        }).WithSummary("Import the project's API specifications, or a HAR file, into the twin");

        // AI understanding: a model proposes entities, features, lifecycles and rules from the twin and the knowledge; a
        // verifier keeps only what the twin's evidence supports. The model must be named: no provider default is used.
        g.MapPost("/{id}/understand", async (string id, UnderstandDto d, ProjectStore store, ModelGateway models, HunterOptions opts, CancellationToken ct) =>
        {
            if (await store.GetAsync(id, ct) is not { } p) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(d.Model)) return Fail("Name the model to use, for example ollama:qwen2.5:7b.");
            if (p.TwinVersion == 0 || await store.TwinAsync(id, p.TwinVersion, ct) is not { } twin) return Fail("The project has no app twin yet: import its specifications or run a hunt first.");
            var k = p.KnowledgeVersion > 0 ? await store.KnowledgeAsync(id, p.KnowledgeVersion, ct) : null;
            var all = SkillPacks.LoadAll(opts);
            var packs = all.Where(x => p.SkillPacks.Contains(x.Name, StringComparer.OrdinalIgnoreCase)).ToList();
            ResolvedModel m;
            try { m = await models.ResolveAsync(d.Model.Trim(), ct); }
            catch (Exception e) when (e is ProviderExecutionException or InvalidOperationException) { return Fail(e.Message); }
            var hash = Twin.Understanding.InputHash(Twin.Understanding.Prompt(p, twin, k, packs, m.Provider is "Ollama" or "Local"));
            if (d.Force != true && twin.Understanding?.InputHash == hash)
                return Results.Ok(new { twinVersion = twin.Version, changed = false, note = "Nothing new since the last understanding; send force to ask again." });
            Twin.Understanding.Result r;
            try { r = await Twin.Understanding.RunAsync(models, m, p, twin, k, packs, ct); }
            catch (Exception e) when (e is ProviderExecutionException or HttpRequestException) { return Fail($"The model call failed: {e.Message}", 502); }
            if (r.Error is not null) return Fail(r.Error, 502);
            await store.SaveTwinAsync(r.Twin, ct);
            p.TwinVersion = r.Twin.Version;
            await store.SaveAsync(p, ct);
            return Results.Ok(new { twinVersion = r.Twin.Version, changed = true, r.Proposed, r.Admitted, rejected = r.Twin.Rejected, r.Twin.Understanding, understood = r.Twin.Changes.Understood });
        }).WithSummary("AI understanding of the app, checked by the verifier");

        g.MapGet("/{id}/journeys", async (string id, ProjectStore store, CancellationToken ct) =>
        {
            if (await store.GetAsync(id, ct) is not { } p) return Results.NotFound();
            var journeys = await store.JourneysAsync(id, ct);
            var twin = p.TwinVersion > 0 ? await store.TwinAsync(id, p.TwinVersion, ct) : null;
            return Results.Ok(new
            {
                journeys = journeys.Select(j => new { j.Id, j.Name, j.Role, j.Source, j.Outcome, j.OutcomeStatus, steps = j.Steps.Count, j.Status, j.Runs, j.Stories, j.Features, j.FromHunt, j.ProvenAt, j.Spec }),
                planned = Journeys.JourneyMiner.Planned(p, twin, journeys).Select(j => new { j.Id, j.Name, j.Role, j.Outcome, j.Stories, j.Features }),
            });
        }).WithSummary("The journey library (mined, proven) and the planned journeys no hunt has reached yet");

        g.MapGet("/{id}/coverage", async (string id, ProjectStore store, CancellationToken ct) =>
        {
            if (await store.GetAsync(id, ct) is not { } p) return Results.NotFound();
            var k = p.KnowledgeVersion > 0 ? await store.KnowledgeAsync(id, p.KnowledgeVersion, ct) : null;
            var twin = p.TwinVersion > 0 ? await store.TwinAsync(id, p.TwinVersion, ct) : null;
            var journeys = await store.JourneysAsync(id, ct);
            var stories = Journeys.Coverage.Stories(k, twin, journeys);
            return Results.Ok(new
            {
                stories, covered = stories.Count(s => s.Covered), total = stories.Count,
                roles = p.Roles.Select(r => new { role = r.Id, proven = journeys.Count(j => j.Role == r.Id && j.Status == "proven"), journeys = journeys.Count(j => j.Role == r.Id) }),
            });
        }).WithSummary("Per story: the features, rules and proven journeys that check it");

        // Runs: what changed since the baseline (the last accepted run). A run's snapshot is taken once its hunts finish;
        // the first finished run becomes the baseline; later ones are accepted explicitly (with review, P8).
        g.MapGet("/{id}/runs", async (string id, ProjectStore store, HuntStore hunts, CancellationToken ct) =>
        {
            if (await store.GetAsync(id, ct) is not { } p) return Results.NotFound();
            var runs = await store.RunsAsync(id, ct);
            // Oldest first, so the first run to finish becomes the baseline, never a later one.
            foreach (var r in runs.Where(r => r.Snapshot is null).OrderBy(r => r.StartedAt)) await Settle(p, r, store, hunts, ct);
            return Results.Ok(runs.Select(r => new { r.Id, r.Kind, r.Hunts, r.StartedAt, finished = r.Snapshot is not null, r.Accepted, r.AcceptedAt, baseline = r.Id == p.BaselineRun,
                findings = r.Snapshot?.Findings.Count, journeysProven = r.Snapshot?.Journeys.Count(j => j.Value == "proven") }));
        }).WithSummary("The project's runs");

        g.MapGet("/{id}/runs/{run}", async (string id, string run, ProjectStore store, HuntStore hunts, CancellationToken ct) =>
        {
            if (await store.GetAsync(id, ct) is not { } p || await store.RunAsync(id, run, ct) is not { } r) return Results.NotFound();
            if (r.Snapshot is null) await Settle(p, r, store, hunts, ct);
            if (r.Snapshot is null) return Results.Ok(new { run = r, finished = false });
            var baseline = p.BaselineRun is { } b && b != r.Id ? await store.RunAsync(id, b, ct) : null;
            var twin = p.TwinVersion > 0 ? await store.TwinAsync(id, r.Snapshot.TwinVersion, ct) : null;
            return Results.Ok(new { run = r, finished = true, isBaseline = p.BaselineRun == r.Id, comparison = Runs.RunCompare.Compare(baseline?.Snapshot, r.Snapshot, twin, baseline?.Id, p.AcceptedBehaviour) });
        }).WithSummary("A run, and what changed since the baseline");

        g.MapPost("/{id}/runs/{run}/accept", async (string id, string run, ProjectStore store, HuntStore hunts, CancellationToken ct) =>
        {
            if (await store.GetAsync(id, ct) is not { } p || await store.RunAsync(id, run, ct) is not { } r) return Results.NotFound();
            if (r.Snapshot is null) await Settle(p, r, store, hunts, ct);
            if (r.Snapshot is null) return Fail("The run has not finished.", 409);
            r.Accepted = true; r.AcceptedAt = DateTime.UtcNow; r.AcceptedBy = "operator";
            await store.SaveRunAsync(r, ct);
            p.BaselineRun = r.Id; await store.SaveAsync(p, ct);
            return Results.Ok(new { baseline = r.Id });
        }).WithSummary("Make a finished run the baseline");

        g.MapGet("/{id}/permissions", async (string id, ProjectStore store, CancellationToken ct) =>
        {
            if (await store.GetAsync(id, ct) is not { } p) return Results.NotFound();
            return await store.TwinAsync(id, p.TwinVersion, ct) is { } t ? Results.Ok(Twin.TwinBuilder.Permissions(t, p)) : Results.NotFound();
        }).WithSummary("Endpoint by role: allowed, denied, error or unknown, as observed");

        Review.ReviewEndpoints.MapReview(g);
        Learning.LearningEndpoints.MapLearning(g);
        Workflow.WorkflowEndpoints.MapWorkflow(g);
    }

    // Takes a run's snapshot once all of its hunts have finished; the first finished run becomes the baseline.
    internal static async Task Settle(Project p, Runs.ProjectRun r, ProjectStore store, HuntStore hunts, CancellationToken ct)
    {
        var list = new List<Hunt>();
        foreach (var hid in r.Hunts) if (await hunts.GetAsync(hid, ct) is { } h) list.Add(h);
        if (list.Count < r.Hunts.Count || list.Any(h => h.Status is "Queued" or "Running" or "Confirming")) return;
        var twin = p.TwinVersion > 0 ? await store.TwinAsync(p.Id, p.TwinVersion, ct) : null;
        var k = p.KnowledgeVersion > 0 ? await store.KnowledgeAsync(p.Id, p.KnowledgeVersion, ct) : null;
        r.Snapshot = Runs.RunCompare.Snapshot(list, twin, await store.JourneysAsync(p.Id, ct), k, p.Triage.Values.Where(t => t.Suppresses).Select(t => t.Key).ToHashSet());
        // An earlier run still unfinished takes precedence as the first baseline.
        var earlierOpen = (await store.RunsAsync(p.Id, ct)).Any(x => x.Snapshot is null && x.StartedAt < r.StartedAt && x.Id != r.Id);
        if (p.BaselineRun is null && !earlierOpen) { r.Accepted = true; r.AcceptedAt = DateTime.UtcNow; r.AcceptedBy = "first run"; p.BaselineRun = r.Id; await store.SaveAsync(p, ct); }
        // The baseline update policy (P8): a later run whose only changes are improvements advances the baseline by
        // itself; one with new findings, regressions or unexplained changes waits for review.
        else if (p.BaselineRun is { } b && b != r.Id && await store.RunAsync(p.Id, b, ct) is { Snapshot: { } bs } baseRun && baseRun.StartedAt < r.StartedAt
            && Runs.RunCompare.Compare(bs, r.Snapshot, twin, b, p.AcceptedBehaviour).OnlyImprovements)
        { r.Accepted = true; r.AcceptedAt = DateTime.UtcNow; r.AcceptedBy = "auto: nothing needing review"; p.BaselineRun = r.Id; await store.SaveAsync(p, ct); }
        await store.SaveRunAsync(r, ct);
    }

    internal static async Task<(string? Id, IResult Result)> StartProjectHunt(Project p, ProjectHuntDto d, string? after, ProjectStore store, KnowledgeService knowledge,
        HuntStore hunts, HuntRunner runner, BrowserWorkerLauncher launcher, HunterOptions opts, DemoShopClient demo, ModelGateway models, CancellationToken ct)
    {
        var id = p.Id;
        var env = d.Environment is null ? p.Environments.FirstOrDefault() : p.Environments.FirstOrDefault(e => e.Name.Equals(d.Environment, StringComparison.OrdinalIgnoreCase));
        if (env is null) return (null, Fail($"Project '{id}' has no environment '{d.Environment}'."));
        var role = d.Role is null ? p.Roles.FirstOrDefault(r => r.Secrets.Count > 0) ?? p.Roles.FirstOrDefault() : p.Roles.FirstOrDefault(r => r.Id == d.Role);
        if (d.Role is not null && role is null) return (null, Fail($"Project '{id}' has no role '{d.Role}'."));

        KnowledgeSnapshot? k;
        try { k = d.Sync == false && p.KnowledgeVersion > 0 ? await store.KnowledgeAsync(id, p.KnowledgeVersion, ct) : (await knowledge.SyncAsync(p, ct)).Snapshot; }
        catch (Exception e) when (e is KnowledgeSourceException or HttpRequestException or IOException) { return (null, Fail($"Knowledge sync failed: {e.Message}", 502)); }
        var wanted = d.Stories is { Length: > 0 } keys ? k?.Stories.Where(s => keys.Contains(s.Key, StringComparer.OrdinalIgnoreCase)).ToList() : k?.Stories;

        var all = await store.SecretsAsync(id, ct);
        var secrets = new Dictionary<string, string>();
        foreach (var (name, secret) in role?.Secrets ?? [])
            if (all.TryGetValue(secret, out var v)) secrets[name] = v; else return (null, Fail($"The project secret '{secret}' for role '{role!.Id}' is not set."));
        if (env.ResetSecret is { } rs && all.TryGetValue(rs, out var rv)) secrets[rs] = rv;

        var mission = d.Mission ?? p.Mission;
        if (p.OutOfScope.Count > 0) mission = $"{mission} Out of scope: {string.Join("; ", p.OutOfScope)}.".Trim();
        if (d.Mission is null && Learning.ChangeAnalysis.Priorities(p.Triage.Values).Take(5).ToList() is { Count: > 0 } hot) mission = $"{mission} Areas where real bugs were found before: {string.Join(", ", hot)}.".Trim();
        var dto = new HuntCreateDto(env.Url, mission, d.MaxSteps, d.Connection, secrets, null, d.Strategy,
            Probe: d.Probe, Confirm: d.Confirm, Shrink: d.Shrink, SetupUrl: env.ResetUrl, SetupSecret: env.ResetUrl is null ? null : env.ResetSecret,
            Stories: wanted?.Take(10).Select(s => new StoryDto(s.Key, s.Title, s.Criteria, s.Version, k!.StorySource)).ToArray(),
            PlannerConnection: d.PlannerConnection, MaxCostUsd: d.MaxCostUsd, Grounding: k is null ? null : KnowledgeDiffer.Grounding(k),
            ProjectId: p.Id, KnowledgeVersion: k?.Version, Environment: env.Name, Role: role?.Id, SkillPacks: p.SkillPacks.ToArray(), AfterHunt: after);
        return await HunterEndpoints.StartHuntCore(dto, hunts, runner, launcher, opts, demo, models, ct);
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{1,39}$")] private static partial Regex IdPattern();
}

// The two demo apps as projects, so a fresh install can hunt them at once. Created only when missing; edits are kept.
public static class DemoProjects
{
    public static async Task EnsureAsync(ProjectStore store, HunterOptions hunter, SimulatorOptions sim, string? groundTruth, CancellationToken ct)
    {
        foreach (var (id, name, url, mission, project, skill, roles, accounts) in new[]
        {
            ("shop", "Demo shop", hunter.DemoShopUrl, "Find functional bugs in browsing, search, cart, coupons, checkout and order management.", "DEMO", "ecommerce",
                new List<ProjectRole>
                {
                    new() { Id = "shopper", Name = "Shopper", Secrets = new() { ["email"] = "shopper-email", ["password"] = "shopper-password" } },
                    new() { Id = "staff", Name = "Staff", Secrets = new() { ["email"] = "staff-email", ["password"] = "staff-password" }, Exclusive = ["/api/staff/"] },
                },
                new Dictionary<string, string?> { ["shopper-email"] = "shopper01@shop.demo", ["shopper-password"] = "Shop123!", ["staff-email"] = "staff@shop.demo", ["staff-password"] = "Staff123!" }),
            ("hotel", "Demo hotel", hunter.DemoHotelUrl, "Find functional bugs in searching rooms, booking a stay, cancelling bookings and the front desk.", "HOTEL", "bookings",
                new List<ProjectRole>
                {
                    new() { Id = "guest", Name = "Guest" },
                    new() { Id = "front-desk", Name = "Front desk", Secrets = new() { ["email"] = "desk-email", ["password"] = "desk-password" }, Exclusive = ["/api/staff/"] },
                },
                new Dictionary<string, string?> { ["desk-email"] = "desk@hotel.demo", ["desk-password"] = "Desk123!" }),
        })
        {
            if (string.IsNullOrWhiteSpace(url)) continue;
            if (await store.GetAsync(id, ct) is { } existing)
            {
                // Projects created before specification import existed get their app's OpenAPI document.
                if (existing.Specs.Count == 0) { existing.Specs = [new() { Kind = "openapi", Url = url.TrimEnd('/') + "/openapi/v1.json" }]; await store.SaveAsync(existing, ct); }
                // ...and staff roles their staff-only area, for the permission predictions.
                foreach (var r in existing.Roles.Where(r => r.Id is "staff" or "front-desk" && r.Exclusive.Count == 0)) { r.Exclusive = ["/api/staff/"]; await store.SaveAsync(existing, ct); }
                continue;
            }
            var app = id;
            var bySim = !string.IsNullOrWhiteSpace(sim.Url);
            var p = new Project
            {
                Id = id, Name = name, Mission = mission, SkillPacks = [skill], Specs = [new() { Kind = "openapi", Url = url!.TrimEnd('/') + "/openapi/v1.json" }],
                Environments = [new() { Name = "local", Url = url!, ResetUrl = url!.TrimEnd('/') + "/api/test/reset", ResetSecret = "reset-key" }],
                Roles = roles,
                Knowledge = bySim
                    ? new() { Stories = new() { Kind = "jira", BaseUrl = sim.Url, Key = project, Email = sim.Email, TokenSecret = "atlassian-token" },
                              Documents = new() { Kind = "confluence", BaseUrl = sim.Url, Key = project, Email = sim.Email, TokenSecret = "atlassian-token" } }
                    : groundTruth is null ? new()
                    : new() { Stories = new() { Kind = "files", Path = Path.Combine(groundTruth, app, "knowledge", "stories.json") },
                              Documents = new() { Kind = "files", Path = Path.Combine(groundTruth, app, "knowledge", "pages") } }
            };
            await store.SaveAsync(p, ct);
            if (hunter.DemoShopKey is { Length: > 0 }) accounts["reset-key"] = hunter.DemoShopKey;
            if (bySim && sim.ApiToken is { Length: > 0 }) accounts["atlassian-token"] = sim.ApiToken;
            await store.SetSecretsAsync(id, accounts, ct);
        }
    }

    public static string? FindGroundTruth(string start)
    {
        for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "ground-truth"))) return Path.Combine(d.FullName, "ground-truth");
        return null;
    }
}
