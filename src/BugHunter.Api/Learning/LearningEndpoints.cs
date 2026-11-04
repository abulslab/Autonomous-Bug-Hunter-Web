using BugHunter.Api.Hunter;
using BugHunter.Api.Models;
using BugHunter.Api.Projects;

namespace BugHunter.Api.Learning;

public record TriageDto(string? Hunt, string? Finding, string? Outcome, string? Note, string? By);
public record ChangeHuntDto(string? Environment, string? Role, string? Strategy, string? Connection, int? MaxSteps, bool? Confirm);

public static class LearningEndpoints
{
    static IResult Fail(string message, int status = 400) => Results.Problem(message, statusCode: status);

    // The pending change, with the twin's new areas since the baseline added.
    internal static async Task<ChangeImpact> ImpactAsync(Project p, ProjectStore store, CancellationToken ct)
    {
        var i = p.PendingChange ?? new ChangeImpact { KnowledgeVersion = p.KnowledgeVersion };
        var since = p.BaselineRun is { } b && await store.RunAsync(p.Id, b, ct) is { Snapshot: { } s } ? s.TwinVersion : int.MaxValue;
        i.NewAreas = ChangeAnalysis.NewAreas(await store.TwinHistoryAsync(p.Id, ct), since);
        return i;
    }

    public static void MapLearning(this RouteGroupBuilder g)
    {
        g.MapGet("/{id}/impact", async (string id, ProjectStore store, CancellationToken ct) =>
            await store.GetAsync(id, ct) is { } p ? Results.Ok(await ImpactAsync(p, store, ct)) : Results.NotFound())
            .WithSummary("What changed since the last change-driven hunt: stories, the features, rules, journeys and endpoints they touch, and new areas of the app");

        // A change-driven hunt: the changed stories first, the affected and new areas named in its mission; the journeys
        // that the change made unproven are replayed first by the engine.
        g.MapPost("/{id}/hunts/changed", async (string id, ChangeHuntDto d, ProjectStore store, KnowledgeService knowledge, HuntStore hunts, HuntRunner runner,
            BrowserWorkerLauncher launcher, HunterOptions opts, DemoShopClient demo, ModelGateway models, CancellationToken ct) =>
        {
            if (await store.GetAsync(id, ct) is not { } p) return Results.NotFound();
            try { await knowledge.SyncAsync(p, ct); } catch (Exception e) when (e is KnowledgeSourceException or HttpRequestException or IOException) { return Fail($"Knowledge sync failed: {e.Message}", 502); }
            p = (await store.GetAsync(id, ct))!;
            var impact = await ImpactAsync(p, store, ct);
            if (impact.Empty) return Fail("Nothing changed since the last change-driven hunt: no story changes and no new areas.", 409);
            var mission = $"{p.Mission} {ChangeAnalysis.Focus(impact, ChangeAnalysis.Priorities(p.Triage.Values))}".Trim();
            var dto = new ProjectHuntDto(d.Environment, d.Role, d.Strategy ?? "Heuristic", d.Connection ?? "heuristic", null, d.MaxSteps ?? 60, null,
                impact.Stories.Count > 0 ? impact.Stories.ToArray() : null, true, d.Confirm ?? true, false, mission, false);
            var (huntId, result) = await ProjectEndpoints.StartProjectHunt(p, dto, null, store, knowledge, hunts, runner, launcher, opts, demo, models, ct);
            if (huntId is null) return result;
            await store.SaveRunAsync(new Runs.ProjectRun { Id = $"R-{huntId[2..]}", ProjectId = id, Kind = "change", Hunts = [huntId] }, ct);
            p = (await store.GetAsync(id, ct))!;
            p.PendingChange = null; await store.SaveAsync(p, ct);
            return Results.Accepted($"/api/hunter/hunts/{huntId}", new { id = huntId, impact, mission });
        }).RequireRateLimiting("hunts").WithSummary("Start a hunt that tests what changed first");

        // Triage outcomes are kept by finding key, so they apply to the same bug in later runs: not a bug and won't fix
        // are suppressed; bugs make their endpoint a priority for later hunts.
        g.MapPost("/{id}/triage", async (string id, TriageDto d, ProjectStore store, HuntStore hunts, CancellationToken ct) =>
        {
            if (await store.GetAsync(id, ct) is not { } p) return Results.NotFound();
            if (!TriageRecord.Outcomes.Contains(d.Outcome)) return Fail($"Outcome is one of {string.Join(", ", TriageRecord.Outcomes)}.");
            if (d.Hunt is null || await hunts.GetAsync(d.Hunt, ct) is not { } h || h.ProjectId != id) return Fail("Name a hunt of this project.");
            if (h.Findings.FirstOrDefault(f => f.Id == d.Finding) is not { } f) return Fail($"Hunt {d.Hunt} has no finding {d.Finding}.");
            var ep = f.RequestMethod is { } m && f.RequestPath is { } path ? $"{m} {Probes.FoldIds(path.Split('?')[0])}" : null;
            var t = new TriageRecord(f.Key, f.Title, d.Outcome!, d.Note?.Trim(), $"person:{(string.IsNullOrWhiteSpace(d.By) ? "operator" : d.By.Trim())}", DateTime.UtcNow, ep, p.Triage.GetValueOrDefault(f.Key)?.IssueKey, f.RootCause);
            p.Triage[f.Key] = t; await store.SaveAsync(p, ct);
            return Results.Ok(t);
        }).WithSummary("Record a triage outcome for a finding: bug, not-a-bug, known-issue or wont-fix");

        g.MapGet("/{id}/learning", async (string id, ProjectStore store, HuntStore hunts, CancellationToken ct) =>
        {
            if (await store.GetAsync(id, ct) is not { } p) return Results.NotFound();
            var twin = p.TwinVersion > 0 ? await store.TwinAsync(id, p.TwinVersion, ct) : null;
            var mine = (await hunts.ListAsync(ct)).Where(h => h.ProjectId == id).ToList();
            return Results.Ok(new
            {
                library = ChangeAnalysis.Library(twin, mine),
                stale = twin?.Rules.Where(r => r.Status == "stale").Select(r => new { r.On, r.Check, r.Statement, r.Story }) ?? [],
                retired = p.RetiredRules.Select(k => new { rule = k, reason = twin?.Rejected.LastOrDefault(x => x.Kind == "rule" && k.EndsWith(" " + x.Name))?.Reason }).ToList(),
                triage = p.Triage.Values.OrderByDescending(t => t.At),
                suppressed = p.Triage.Values.Count(t => t.Suppresses),
                priorities = ChangeAnalysis.Priorities(p.Triage.Values),
                pending = await ImpactAsync(p, store, ct),
            });
        }).WithSummary("What the project has learned: the rule library, stale and retired rules, triage outcomes, suppressions and priorities");
    }
}
