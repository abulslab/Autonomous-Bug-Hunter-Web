using BugHunter.Api.Hunter;
using BugHunter.Api.Journeys;
using BugHunter.Api.Projects;
using BugHunter.Api.Twin;

namespace BugHunter.Api.Learning;

// What a change touches, found by traceability: story -> features, rules and journeys -> endpoints. A change-driven hunt
// tests it first.
public class ChangeImpact
{
    public DateTime At { get; set; } = DateTime.UtcNow;
    public int KnowledgeVersion { get; set; }
    public List<string> Stories { get; set; } = [];        // changed or added
    public List<string> Features { get; set; } = [];
    public List<string> Endpoints { get; set; } = [];
    public List<string> Rules { get; set; } = [];          // "POST /api/bookings total == ..." made stale: rewritten from the new criteria
    public List<string> Journeys { get; set; } = [];       // to prove again
    public List<string> NewAreas { get; set; } = [];       // from the twin diff: endpoints and pages new since the baseline
    public bool Empty => Stories.Count + NewAreas.Count == 0;
}

// A triage outcome on a finding, by finding key, so it applies to the same bug in every later run.
public record TriageRecord(string Key, string Title, string Outcome, string? Note, string By, DateTime At, string? Endpoint, string? IssueKey = null, string? Cause = null)
{
    public static readonly string[] Outcomes = ["bug", "not-a-bug", "known-issue", "wont-fix"];
    // Not a bug and won't fix are suppressed: no longer new findings, not counted against release readiness.
    public bool Suppresses => Outcome is "not-a-bug" or "wont-fix";
}

public record LibraryRule(string On, string Check, string Statement, string? Story, int Hunts, int Holds, int Violations);

public static class ChangeAnalysis
{
    public static ChangeImpact ForStories(KnowledgeSnapshot k, AppTwin? twin, IEnumerable<Journey> journeys)
    {
        var i = new ChangeImpact { KnowledgeVersion = k.Version };
        i.Stories = k.Diff.StoriesChanged.Select(c => c.Key).Concat(k.Diff.StoriesAdded).Distinct().ToList();
        var stories = i.Stories.ToHashSet();
        var features = twin?.Features.Where(f => f.Stories.Any(stories.Contains)).ToList() ?? [];
        i.Features = features.Select(f => f.Name).ToList();
        var rules = twin?.Rules.Where(r => r.Story is { } s && stories.Contains(s)).ToList() ?? [];
        i.Rules = rules.Select(r => $"{r.On} {r.Check}").ToList();
        i.Endpoints = features.SelectMany(f => f.Endpoints).Concat(rules.Select(r => r.On)).Distinct().Order().ToList();
        var eps = i.Endpoints.ToHashSet();
        i.Journeys = journeys.Where(j => j.Stories.Any(stories.Contains) || eps.Contains(j.Outcome)).Select(j => j.Id).ToList();
        return i;
    }

    // New areas: what twin versions after the baseline's added.
    public static List<string> NewAreas(IEnumerable<AppTwin> history, int sinceVersion) =>
        history.Where(t => t.Version > sinceVersion)
            .SelectMany(t => t.Changes.EndpointsAdded.Concat(t.Changes.StatesAdded.Select(s => $"page {s}")).Concat(t.Changes.EndpointsDeclared))
            .Distinct().Take(20).ToList();

    // Applies a story change: rules of changed stories go stale (not checked until understanding rewrites them from the
    // new criteria and a reviewer accepts them again); affected journeys are proven again, and reviewed again.
    public static void Apply(ChangeImpact i, AppTwin? twin, IEnumerable<Journey> journeys)
    {
        foreach (var r in twin?.Rules.Where(r => i.Rules.Contains($"{r.On} {r.Check}")) ?? []) r.Status = "stale";
        foreach (var j in journeys.Where(j => i.Journeys.Contains(j.Id)))
        {
            if (j.Status == "proven") j.Status = "candidate";
            j.Review = j.Review == "rejected" ? "rejected" : null;
        }
    }

    // The mission of a change-driven hunt: test the change first.
    public static string Focus(ChangeImpact i, IEnumerable<string> priorities)
    {
        var parts = new List<string>();
        if (i.Stories.Count > 0) parts.Add($"Changed stories, test first: {string.Join(", ", i.Stories)}");
        if (i.Features.Count > 0) parts.Add($"affected features: {string.Join(", ", i.Features)}");
        if (i.Endpoints.Count > 0) parts.Add($"affected endpoints: {string.Join(", ", i.Endpoints.Take(10))}");
        if (i.NewAreas.Count > 0) parts.Add($"new since the last baseline, explore first: {string.Join(", ", i.NewAreas.Take(10))}");
        var hot = priorities.Take(5).ToList();
        if (hot.Count > 0) parts.Add($"areas where real bugs were found before: {string.Join(", ", hot)}");
        return parts.Count == 0 ? "" : string.Join("; ", parts) + ".";
    }

    // Priorities: endpoints with findings triaged as real bugs, most first.
    public static List<string> Priorities(IEnumerable<TriageRecord> triage) =>
        triage.Where(t => t.Outcome is "bug" or "known-issue" && t.Endpoint is not null).GroupBy(t => t.Endpoint!).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key).ToList();

    // The rule library: accepted rules with how they did across the project's hunts. They are reused without AI.
    public static List<LibraryRule> Library(AppTwin? twin, IEnumerable<Hunt> hunts)
    {
        var list = hunts.ToList();
        return (twin?.Rules.Where(r => r.Status == "accepted") ?? []).Select(r =>
        {
            var runs = list.SelectMany(h => h.Invariants.Where(x => x.On == r.On && x.Check == r.Check)).ToList();
            return new LibraryRule(r.On, r.Check, r.Statement, r.Story, runs.Count(x => x.Checks > 0), runs.Sum(x => x.Holds), runs.Sum(x => x.Violations));
        }).ToList();
    }
}
