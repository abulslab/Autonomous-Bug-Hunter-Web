using System.Text;
using BugHunter.Api.Hunter;
using BugHunter.Api.Journeys;
using BugHunter.Api.Learning;
using BugHunter.Api.Projects;
using BugHunter.Api.Runs;
using BugHunter.Api.Twin;

namespace BugHunter.Api.Workflow;

// Where findings and reports go. IssueProject: the Jira project bugs are filed in (default: the stories' project).
// ReportSpace: the Confluence space the readiness report is published to, kept apart from the product documentation
// so the report never becomes knowledge.
public class WorkflowConfig
{
    public string? IssueProject { get; set; }
    public string ReportSpace { get; set; } = "QA";
}

public record StoryLine(string Key, string Title, int Version, bool Covered, List<string> ProvenJourneys, List<string> Rules);
public record JourneyLine(string Name, string Role, string Status, string? Review);
// Retested false: a baseline bug no run since the baseline tried again; it is still assumed open. Cause: findings that
// share one are one bug.
public record BugLine(string Title, string Severity, string Provenance, string? Reproduced, string Hunt, string Finding, string? Triage, string? Issue, bool Retested = true, string? Cause = null);

public class ReadinessReport
{
    public string ProjectId { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime At { get; set; } = DateTime.UtcNow;
    public string Verdict { get; set; } = "not-ready";      // ready | at-risk | not-ready
    public List<string> Blockers { get; set; } = [];
    public List<string> Risks { get; set; } = [];
    public string? Run { get; set; }
    public string? Baseline { get; set; }
    public int KnowledgeVersion { get; set; }
    public int TwinVersion { get; set; }
    public List<StoryLine> Stories { get; set; } = [];
    public int StoriesCovered => Stories.Count(s => s.Covered);
    public List<JourneyLine> Journeys { get; set; } = [];
    public List<BugLine> OpenBugs { get; set; } = [];
    public int OpenCauses => OpenBugs.Select(b => b.Cause ?? b.Title).Distinct().Count();
    public List<string> Runs { get; set; } = [];        // the runs since the baseline that the report covers
    public int Suppressed { get; set; }
    public RunComparison? Changes { get; set; }
    public List<string> AcceptedBehaviour { get; set; } = [];
    public int PendingReviews { get; set; }
    public ChangeImpact? UntestedChange { get; set; }
}

public static class Readiness
{
    // The release decision, from what is proven: blockers make it not ready, risks make it at risk.
    public static ReadinessReport Build(Project p, KnowledgeSnapshot? k, AppTwin? twin, List<Journey> journeys, ProjectRun? latest, RunComparison? comparison,
        IReadOnlyDictionary<string, Hunt> hunts, IEnumerable<Review.ReviewItem> review)
    {
        var r = new ReadinessReport { ProjectId = p.Id, Name = p.Name, Run = latest?.Id, Baseline = p.BaselineRun, KnowledgeVersion = k?.Version ?? 0, TwinVersion = twin?.Version ?? 0 };
        r.Stories = Coverage.Stories(k, twin, journeys).Select(s => new StoryLine(s.Key, s.Title, s.Version, s.Covered, s.ProvenJourneys, s.Rules)).ToList();
        r.Journeys = journeys.Where(j => j.Source == "mined" && j.Review != "rejected").Select(j => new JourneyLine(j.Name, j.Role, j.Status, j.Review)).ToList();
        foreach (var (key, f) in latest?.Snapshot?.Findings ?? [])
        {
            var finding = hunts.GetValueOrDefault(f.Hunt)?.Findings.FirstOrDefault(x => x.Id == f.Id);
            var t = p.Triage.GetValueOrDefault(key);
            r.OpenBugs.Add(new(f.Title, finding?.Severity ?? "Medium", f.Provenance, f.Reproduced, f.Hunt, f.Id, t?.Outcome, t?.IssueKey, true, f.Cause ?? key));
        }
        foreach (var f in comparison?.FindingsNotRetested ?? [])
        {
            var t = p.Triage.Values.FirstOrDefault(x => x.Title == f.Title);
            r.OpenBugs.Add(new(f.Title, "Medium", f.Provenance, f.Reproduced, f.Hunt, f.Id, t?.Outcome, t?.IssueKey, false, f.Cause ?? f.Title));
        }
        r.Suppressed = p.Triage.Values.Count(t => t.Suppresses);
        r.Changes = comparison;
        r.AcceptedBehaviour = p.AcceptedBehaviour.Select(kv => $"{kv.Key}: {kv.Value}").ToList();
        var items = review.ToList();
        r.PendingReviews = items.Count(i => i.Status == "pending");
        r.UntestedChange = p.PendingChange is { Stories.Count: > 0 } c ? c : null;

        if (latest?.Snapshot is null) r.Blockers.Add("No finished run: nothing has been tested.");
        var confirmed = r.OpenBugs.Where(b => b.Reproduced == "Confirmed" && b.Triage != "known-issue").GroupBy(b => b.Cause ?? b.Title).ToList();
        if (confirmed.Count > 0) r.Blockers.Add($"{confirmed.Count} confirmed bug{(confirmed.Count == 1 ? "" : "s")} open: {string.Join("; ", confirmed.Take(3).Select(g => g.Count() == 1 ? g.First().Title : $"{g.First().Title} (and {g.Count() - 1} more endpoints, one cause)"))}");
        if (comparison?.JourneysRegressed.Count > 0) r.Blockers.Add($"Journeys regressed since the baseline: {string.Join(", ", comparison.JourneysRegressed)}");
        if (items.Count(i => i.Status == "pending" && i.Blocking) is > 0 and var nb) r.Blockers.Add($"{nb} blocking review item{(nb == 1 ? "" : "s")} undecided");

        if (r.OpenBugs.Count(b => !b.Retested) is > 0 and var stale) r.Risks.Add($"{stale} bug{(stale == 1 ? "" : "s")} from the baseline not retested since: assumed still open");
        if (r.OpenBugs.Count(b => b.Reproduced != "Confirmed") is > 0 and var unconfirmed) r.Risks.Add($"{unconfirmed} finding{(unconfirmed == 1 ? "" : "s")} not confirmed by replay");
        if (r.OpenBugs.Count(b => b.Triage == "known-issue") is > 0 and var known) r.Risks.Add($"{known} known issue{(known == 1 ? "" : "s")} shipping");
        if (r.UntestedChange is { } u) r.Risks.Add($"Changed stories not hunted yet: {string.Join(", ", u.Stories)}");
        if (r.PendingReviews > 0) r.Risks.Add($"{r.PendingReviews} review item{(r.PendingReviews == 1 ? "" : "s")} waiting");
        if (r.Stories.Count > 0 && r.StoriesCovered < 0.8 * r.Stories.Count) r.Risks.Add($"Story coverage {r.StoriesCovered}/{r.Stories.Count}");
        if (comparison?.BehaviourChanged.Count(b => b.NeedsReview) is > 0 and var changed) r.Risks.Add($"{changed} behaviour change{(changed == 1 ? "" : "s")} no story explains");
        if (r.Journeys.Count(j => j.Status is "flaky" or "failing") is > 0 and var shaky) r.Risks.Add($"{shaky} journey{(shaky == 1 ? "" : "s")} flaky or failing");

        r.Verdict = r.Blockers.Count > 0 ? "not-ready" : r.Risks.Count > 0 ? "at-risk" : "ready";
        return r;
    }

    public static string Markdown(ReadinessReport r)
    {
        var sb = new StringBuilder();
        var label = r.Verdict switch { "ready" => "Ready", "at-risk" => "At risk", _ => "Not ready" };
        sb.AppendLine($"# Release readiness: {r.Name}").AppendLine();
        sb.AppendLine($"**{label}**. Runs {(r.Runs.Count > 0 ? string.Join(", ", r.Runs) : "none")}, baseline {r.Baseline ?? "none"}, knowledge v{r.KnowledgeVersion}, app twin v{r.TwinVersion}, {r.At:yyyy-MM-dd HH:mm} UTC.").AppendLine();
        if (r.Blockers.Count > 0) { sb.AppendLine("## Blockers"); foreach (var b in r.Blockers) sb.AppendLine($"- {b}"); sb.AppendLine(); }
        if (r.Risks.Count > 0) { sb.AppendLine("## Risks"); foreach (var b in r.Risks) sb.AppendLine($"- {b}"); sb.AppendLine(); }
        sb.AppendLine($"## Open bugs ({r.OpenCauses} causes, {r.OpenBugs.Count} findings; {r.Suppressed} suppressed by triage)");
        if (r.OpenBugs.Count == 0) sb.AppendLine("None.");
        else
        {
            sb.AppendLine("| Bug | Severity | Source | Replay | Triage | Issue |").AppendLine("|---|---|---|---|---|---|");
            foreach (var b in r.OpenBugs) sb.AppendLine($"| {b.Title}{(b.Retested ? "" : " (not retested)")} | {b.Severity} | {b.Provenance} | {b.Reproduced ?? "not replayed"} | {b.Triage ?? "untriaged"} | {b.Issue ?? ""} |");
        }
        sb.AppendLine().AppendLine($"## Story coverage ({r.StoriesCovered}/{r.Stories.Count})");
        sb.AppendLine("| Story | Version | Covered | Proven journeys | Rules |").AppendLine("|---|---|---|---|---|");
        foreach (var s in r.Stories) sb.AppendLine($"| {s.Key} {s.Title} | v{s.Version} | {(s.Covered ? "yes" : "no")} | {string.Join(", ", s.ProvenJourneys)} | {s.Rules.Count} |");
        sb.AppendLine().AppendLine($"## Journeys ({r.Journeys.Count(j => j.Status == "proven")}/{r.Journeys.Count} passing)");
        foreach (var j in r.Journeys) sb.AppendLine($"- {j.Name} ({j.Role}): {j.Status}{(j.Review == "accepted" ? ", in the gate" : "")}");
        if (r.Changes is { } c)
        {
            sb.AppendLine().AppendLine($"## Changes since the baseline ({c.BaselineRun})");
            if (c.Empty) sb.AppendLine("Nothing changed.");
            void L(string name, IEnumerable<string> xs) { var list = xs.ToList(); if (list.Count > 0) sb.AppendLine($"- {name}: {string.Join("; ", list)}"); }
            L("New findings", c.FindingsNew.Select(f => f.Title)); L("Fixed", c.FindingsFixed.Select(f => f.Title));
            L("Journeys regressed", c.JourneysRegressed); L("Journeys recovered", c.JourneysRecovered);
            L("Stories changed", c.StoriesChanged);
            L("Behaviour changed", c.BehaviourChanged.Select(b => $"{b.Endpoint}{(b.ExplainedBy.Count > 0 ? $" (by {string.Join(", ", b.ExplainedBy)})" : b.Accepted ? " (accepted)" : " (unexplained)")}"));
            L("Endpoints added", c.EndpointsAdded); L("Endpoints removed", c.EndpointsRemoved);
            L("Coverage gained", c.CoverageGained); L("Coverage lost", c.CoverageLost);
        }
        if (r.AcceptedBehaviour.Count > 0) { sb.AppendLine().AppendLine("## Behaviour accepted as intended"); foreach (var a in r.AcceptedBehaviour) sb.AppendLine($"- {a}"); }
        return sb.ToString();
    }
}
