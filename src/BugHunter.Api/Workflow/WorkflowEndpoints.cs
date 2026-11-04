using BugHunter.Api.Hunter;
using BugHunter.Api.Learning;
using BugHunter.Api.Projects;

namespace BugHunter.Api.Workflow;

public static class WorkflowEndpoints
{
    static IResult Fail(string message, int status = 400) => Results.Problem(message, statusCode: status);

    internal static async Task<ReadinessReport> ReportAsync(Project p, ProjectStore store, HuntStore hunts, CancellationToken ct)
    {
        var review = await Review.ReviewEndpoints.RefreshAsync(p, store, hunts, ct); // settles runs and refreshes the queue
        p = (await store.GetAsync(p.Id, ct))!;
        var k = p.KnowledgeVersion > 0 ? await store.KnowledgeAsync(p.Id, p.KnowledgeVersion, ct) : null;
        var twin = p.TwinVersion > 0 ? await store.TwinAsync(p.Id, p.TwinVersion, ct) : null;
        // The release candidate is every finished run since the baseline (a change-driven hunt and a survey, say), not
        // just the latest: a bug the hunt proved stays open even if the survey that finished after it never went there.
        var finished = (await store.RunsAsync(p.Id, ct)).Where(r => r.Snapshot is not null).OrderBy(r => r.StartedAt).ToList();
        var baseRun = p.BaselineRun is { } b ? finished.FirstOrDefault(r => r.Id == b) : null;
        var since = baseRun is null ? finished : finished.Where(r => r.StartedAt > baseRun.StartedAt).ToList();
        var against = baseRun;
        // When the baseline is the latest run, report on it and compare it with the run it replaced.
        if (since.Count == 0 && baseRun is not null) { since = [baseRun]; against = finished.LastOrDefault(r => r.Accepted && r.StartedAt < baseRun.StartedAt); }
        Runs.ProjectRun? candidate = since.Count == 0 ? null : new Runs.ProjectRun
        {
            Id = string.Join("+", since.Select(r => r.Id)), ProjectId = p.Id, Hunts = since.SelectMany(r => r.Hunts).ToList(),
            Snapshot = Runs.RunCompare.Merge(since.Select(r => r.Snapshot!).ToList()),
        };
        var comparison = candidate is not null && against?.Snapshot is not null ? Runs.RunCompare.Compare(against.Snapshot, candidate.Snapshot!, twin, against.Id, p.AcceptedBehaviour) : null;
        var byId = new Dictionary<string, Hunt>();
        foreach (var hid in candidate?.Hunts ?? []) if (await hunts.GetAsync(hid, ct) is { } h) byId[hid] = h;
        var report = Readiness.Build(p, k, twin, await store.JourneysAsync(p.Id, ct), candidate, comparison, byId, review);
        report.Runs = since.Select(r => r.Id).ToList();
        return report;
    }

    static async Task<AtlassianWriter> WriterAsync(Project p, SourceConfig c, ProjectStore store, HunterOptions opts, IHttpClientFactory http, CancellationToken ct)
    {
        if (await Common.UrlGuard.CheckAsync(c.BaseUrl ?? "", opts.AllowPrivateTargets == true, allowHttp: true) is { } urlErr) throw new KnowledgeSourceException(urlErr);
        var secrets = await store.SecretsAsync(p.Id, ct);
        if (c.TokenSecret is not { } t || !secrets.TryGetValue(t, out var token)) throw new KnowledgeSourceException($"The project secret '{c.TokenSecret}' for {c.Kind} is not set.");
        return new AtlassianWriter(http.CreateClient(), c.BaseUrl!, c.Email ?? "", token);
    }

    public static void MapWorkflow(this RouteGroupBuilder g)
    {
        g.MapGet("/{id}/readiness", async (string id, string? format, ProjectStore store, HuntStore hunts, CancellationToken ct) =>
        {
            if (await store.GetAsync(id, ct) is not { } p) return Results.NotFound();
            var r = await ReportAsync(p, store, hunts, ct);
            return format == "markdown" ? Results.Text(Readiness.Markdown(r), "text/markdown") : Results.Ok(r);
        }).WithSummary("The release readiness report: verdict, blockers and risks, open bugs, story coverage, journeys, changes since the baseline");

        g.MapPut("/{id}/workflow", async (string id, WorkflowConfig w, ProjectStore store, CancellationToken ct) =>
        {
            if (await store.GetAsync(id, ct) is not { } p) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(w.ReportSpace)) return Fail("A report space key is needed.");
            p.Workflow = w; await store.SaveAsync(p, ct);
            return Results.Ok(p.Workflow);
        }).WithSummary("Where findings are filed and reports published");

        // The report as a Confluence page, in its own space: the product documentation is knowledge, the report is not.
        g.MapPost("/{id}/readiness/publish", async (string id, ProjectStore store, HuntStore hunts, HunterOptions opts, IHttpClientFactory http, CancellationToken ct) =>
        {
            if (await store.GetAsync(id, ct) is not { } p) return Results.NotFound();
            if (p.Knowledge.Documents is not { Kind: "confluence" } c) return Fail("The project's documents are not in Confluence.");
            var r = await ReportAsync(p, store, hunts, ct);
            try
            {
                var w = await WriterAsync(p, c, store, opts, http, ct);
                var space = p.Workflow.ReportSpace;
                var (pageId, version) = await w.PublishAsync(space, $"Release readiness: {p.Name}", AtlassianWriter.Storage(Readiness.Markdown(r)), ct);
                return Results.Ok(new { r.Verdict, space, page = pageId, version, url = $"{c.BaseUrl!.TrimEnd('/')}/wiki/pages/{pageId}" });
            }
            catch (Exception e) when (e is KnowledgeSourceException or HttpRequestException) { return Fail($"Publishing failed: {e.Message}", 502); }
        }).WithSummary("Publish the readiness report to Confluence (a new page version each time)");

        // Files a finding as a Jira bug linked to the story it breaks. The same bug (by finding key) is filed once; later
        // sightings become comments. Filing records the triage outcome "bug".
        g.MapPost("/{id}/findings/{hunt}/{finding}/jira", async (string id, string hunt, string finding, ProjectStore store, HuntStore hunts, HunterOptions opts, IHttpClientFactory http, CancellationToken ct) =>
        {
            if (await store.GetAsync(id, ct) is not { } p) return Results.NotFound();
            if (p.Knowledge.Stories is not { Kind: "jira" } c) return Fail("The project's stories are not in Jira.");
            if (await hunts.GetAsync(hunt, ct) is not { } h || h.ProjectId != id || h.Findings.FirstOrDefault(f => f.Id == finding) is not { } f) return Results.NotFound();
            var project = p.Workflow.IssueProject ?? c.Key!;
            var story = f.Requirement;
            try
            {
                var w = await WriterAsync(p, c, store, opts, http, ct);
                // One issue per bug: a finding whose root cause already has an issue (another endpoint of the same leak) comments on it.
                var known = p.Triage.GetValueOrDefault(f.Key) ?? p.Triage.Values.FirstOrDefault(t => t.IssueKey is not null && t.Cause == f.RootCause);
                if (known?.IssueKey is { } existing)
                {
                    await w.CommentAsync(existing, $"Seen again by Bug Hunter in hunt {h.Id}, finding {f.Id}: {f.Actual}", ct);
                    return Results.Ok(new { key = existing, url = w.Browse(existing), created = false });
                }
                var key = await w.FileBugAsync(project, f.Title, AtlassianWriter.BugDescription(h, f, story), ["bug-hunter", f.Provenance.ToLowerInvariant(), .. story is null ? Array.Empty<string>() : [story]], ct);
                if (story is not null) { try { await w.LinkAsync(key, story, ct); } catch (KnowledgeSourceException) { /* the story may live in another project */ } }
                var ep = f.RequestMethod is { } m && f.RequestPath is { } path ? $"{m} {Probes.FoldIds(path.Split('?')[0])}" : null;
                p.Triage[f.Key] = (known ?? new TriageRecord(f.Key, f.Title, "bug", null, "person:operator", DateTime.UtcNow, ep)) with { IssueKey = key, Cause = f.RootCause };
                await store.SaveAsync(p, ct);
                return Results.Ok(new { key, url = w.Browse(key), created = true });
            }
            catch (Exception e) when (e is KnowledgeSourceException or HttpRequestException) { return Fail($"Filing failed: {e.Message}", 502); }
        }).WithSummary("File a finding as a Jira bug, linked to the story it breaks (once per bug; later sightings comment)");
    }
}
