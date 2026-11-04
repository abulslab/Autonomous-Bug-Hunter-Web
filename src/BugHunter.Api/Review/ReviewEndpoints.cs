using BugHunter.Api.Hunter;
using BugHunter.Api.Models;
using BugHunter.Api.Projects;

namespace BugHunter.Api.Review;

public record ReviewDecideDto(bool Accept, string? Reason, string? By);
public record ReviewJudgeDto(string? Model);

public static class ReviewEndpoints
{
    static IResult Fail(string message, int status = 400) => Results.Problem(message, statusCode: status);

    // The queue, refreshed from the project's current state: the twin's unreviewed rules, proven journeys, and the latest
    // finished run's unexplained behaviour changes and baseline update.
    internal static async Task<List<ReviewItem>> RefreshAsync(Project p, ProjectStore store, HuntStore hunts, CancellationToken ct)
    {
        var twin = p.TwinVersion > 0 ? await store.TwinAsync(p.Id, p.TwinVersion, ct) : null;
        var runs = await store.RunsAsync(p.Id, ct);
        foreach (var r in runs.Where(r => r.Snapshot is null).OrderBy(r => r.StartedAt)) await ProjectEndpoints.Settle(p, r, store, hunts, ct);
        var latest = runs.FirstOrDefault(r => r.Snapshot is not null);
        Runs.RunComparison? comparison = null;
        if (latest is not null && latest.Id != p.BaselineRun && p.BaselineRun is { } b && await store.RunAsync(p.Id, b, ct) is { Snapshot: { } bs })
            comparison = Runs.RunCompare.Compare(bs, latest.Snapshot!, twin, b, p.AcceptedBehaviour);
        var items = await store.ReviewAsync(p.Id, ct);
        // A newer finished run supersedes older baseline proposals, and a rule no longer in the twin needs no decision.
        foreach (var old in items.Where(i => i.Status == "pending" && ((i.Kind == "baseline" && i.Data.GetValueOrDefault("run") != latest?.Id)
                     || (i.Kind == "rule" && twin?.Rules.Any(r => r.On == i.Data.GetValueOrDefault("on") && r.Check == i.Data.GetValueOrDefault("check") && r.Status is "inferred" or "verified") != true))).ToList())
        { old.Status = "superseded"; await store.SaveReviewAsync(old, ct); }
        foreach (var i in ReviewQueue.Collect(p, twin, await store.JourneysAsync(p.Id, ct), latest, comparison, items)) { await store.SaveReviewAsync(i, ct); items.Insert(0, i); }
        return items;
    }

    public static void MapReview(this RouteGroupBuilder g)
    {
        g.MapGet("/{id}/review", async (string id, ProjectStore store, HuntStore hunts, CancellationToken ct) =>
        {
            if (await store.GetAsync(id, ct) is not { } p) return Results.NotFound();
            var items = await RefreshAsync(p, store, hunts, ct);
            var agreement = ReviewGate.Agreement(items);
            return Results.Ok(new
            {
                items = items.OrderBy(i => i.Status != "pending").ThenByDescending(i => i.SpotCheck).ThenByDescending(i => i.CreatedAt),
                pending = items.Count(i => i.Status == "pending"), spotChecks = items.Count(i => i.SpotCheck),
                settings = p.Review, agreement = new { agreement.Compared, agreement.Agreed, agreement.Rate },
                plannerModel = p.TwinVersion > 0 ? (await store.TwinAsync(id, p.TwinVersion, ct))?.Understanding?.Model : null,
            });
        }).WithSummary("The review queue: AI-drafted rules, proven journeys, behaviour changes and baseline updates waiting for a decision");

        g.MapPost("/{id}/review/{item}", async (string id, string item, ReviewDecideDto d, ProjectStore store, ReviewService review, CancellationToken ct) =>
        {
            if (await store.GetAsync(id, ct) is not { } p) return Results.NotFound();
            if ((await store.ReviewAsync(id, ct)).FirstOrDefault(i => i.Id == item) is not { } it) return Results.NotFound();
            var by = $"person:{(string.IsNullOrWhiteSpace(d.By) ? "operator" : d.By.Trim())}";
            if (d.By?.Trim().StartsWith("judge", StringComparison.OrdinalIgnoreCase) == true) return Fail("Only the judge endpoint decides as the judge.");
            await review.DecideAsync(p, it, d.Accept, by, d.Reason?.Trim() is { Length: > 0 } r ? r : d.Accept ? "accepted" : "rejected", ct);
            return Results.Ok(it);
        }).WithSummary("Decide a review item (deciding again reverses the earlier decision)");

        g.MapPut("/{id}/review/settings", async (string id, ReviewSettings s, ProjectStore store, CancellationToken ct) =>
        {
            if (await store.GetAsync(id, ct) is not { } p) return Results.NotFound();
            if (s.AgreementThreshold is < 0.5 or > 1 || s.SpotCheckRate is < 0 or > 1 || s.MinAgreementSample is < 1 or > 1000) return Fail("Threshold is 0.5 to 1, spot-check rate 0 to 1, sample 1 to 1000.");
            p.Review = s; await store.SaveAsync(p, ct);
            return Results.Ok(p.Review);
        }).WithSummary("The review gate's judge settings");

        // The judge gives a verdict on every pending item (except behaviour changes, which only a person accepts). Its
        // verdict is applied only when ReviewGate.CanApply allows; otherwise it is advice shown next to the item, and,
        // once a person decides, it counts towards the measured agreement.
        g.MapPost("/{id}/review/judge", async (string id, ReviewJudgeDto d, ProjectStore store, HuntStore hunts, ReviewService review, ModelGateway models, CancellationToken ct) =>
        {
            if (await store.GetAsync(id, ct) is not { } p) return Results.NotFound();
            var name = d.Model?.Trim() is { Length: > 0 } x ? x : p.Review.JudgeModel;
            if (string.IsNullOrWhiteSpace(name)) return Fail("Name the judge model, for example ollama:llama3.2, or set it in the review settings.");
            ResolvedModel m;
            try { m = await models.ResolveAsync(name, ct); }
            catch (Exception e) when (e is ProviderExecutionException or InvalidOperationException) { return Fail(e.Message); }
            var items = await RefreshAsync(p, store, hunts, ct);
            var pending = items.Where(i => i.Status == "pending" && i.Kind != "behaviour").ToList();
            if (pending.Count == 0) return Results.Ok(new { judged = 0, applied = 0, note = "Nothing to judge." });
            var twin = p.TwinVersion > 0 ? await store.TwinAsync(id, p.TwinVersion, ct) : null;
            var k = p.KnowledgeVersion > 0 ? await store.KnowledgeAsync(id, p.KnowledgeVersion, ct) : null;
            var settings = p.Review.JudgeModel == name ? p.Review : new ReviewSettings { JudgeModel = name, Bypass = p.Review.Bypass, AgreementThreshold = p.Review.AgreementThreshold, MinAgreementSample = p.Review.MinAgreementSample, SpotCheckRate = p.Review.SpotCheckRate };
            int judged = 0, applied = 0; var notes = new List<string>();
            foreach (var batch in pending.Chunk(10))
            {
                List<(string Id, bool Accept, string Reason)> verdicts;
                try { verdicts = await ReviewGate.JudgeAsync(models, m, k, batch, ct); }
                catch (Exception e) when (e is ProviderExecutionException or HttpRequestException or OperationCanceledException) { notes.Add($"The judge call failed: {e.Message}"); break; }
                foreach (var (iid, accept, reason) in verdicts)
                {
                    var it = batch.First(i => i.Id == iid);
                    it.Judge = new($"judge:{name}", accept, reason, DateTime.UtcNow);
                    judged++;
                    var (apply, why) = ReviewGate.CanApply(it, settings, ReviewGate.Agreement(items), twin?.Understanding?.Model);
                    if (apply)
                    {
                        it.SpotCheck = ReviewGate.Sampled(it.Id, settings.SpotCheckRate);
                        await review.DecideAsync(p, it, accept, $"judge:{name}", $"{reason} ({why})", ct);
                        applied++;
                    }
                    else { await store.SaveReviewAsync(it, ct); if (notes.Count < 3 && !notes.Contains(why)) notes.Add(why); }
                }
            }
            return Results.Ok(new { judged, applied, spotChecks = pending.Count(i => i.SpotCheck), notes });
        }).WithSummary("Ask the LLM judge; it decides only when the project allows and its agreement with people is high enough");
    }
}
