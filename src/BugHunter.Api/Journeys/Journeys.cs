using System.Text.RegularExpressions;
using BugHunter.Api.Hunter;
using BugHunter.Api.Projects;
using BugHunter.Api.Twin;

namespace BugHunter.Api.Journeys;

// A journey is a happy path: the steps a user of one role takes from the start page to a business outcome (an API
// write that succeeded: a booking made, an order placed, a guest checked in). Journeys are mined from what hunts
// actually did, with no AI, and count only once proven: replayed end to end in fresh sessions without a model, with
// the outcome happening again each time. Proven journeys are the project's regression suite.
public class Journey
{
    public string Id { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
    public string Source { get; set; } = "mined";       // mined (from a hunt) | planned (from the twin's features, no steps yet)
    public string Outcome { get; set; } = "";           // "POST /api/bookings"
    public int OutcomeStatus { get; set; }              // what it answered when recorded
    public List<HuntStep> Steps { get; set; } = [];
    public string? FromHunt { get; set; }
    public List<string> Stories { get; set; } = [];
    public List<string> Features { get; set; } = [];
    public string Status { get; set; } = "candidate";   // candidate | proven | flaky | failing | planned
    public List<JourneyRun> Runs { get; set; } = [];    // latest first
    public string? Spec { get; set; }                   // a Playwright test that walks it and asserts the outcome
    public string? Review { get; set; }                 // accepted (part of the regression gate) | rejected (not a real user path; never replayed)
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProvenAt { get; set; }
}

public record JourneyRun(DateTime At, string Hunt, bool Passed, string? Problem);

public static partial class JourneyMiner
{
    // An outcome: a write the app accepted, other than signing in or out and the test harness.
    public static bool IsOutcome(string method, string path, int status) =>
        method is "POST" or "PUT" or "PATCH" or "DELETE" && status is >= 200 and < 300
        && !NotOutcome().IsMatch(path);

    static readonly string[] Replayable = ["open", "click", "fill", "select", "check", "press", "navigate", "back"];

    // One candidate per (role, outcome): the shortest prefix of the hunt that reached it. The whole prefix is kept so a
    // replay recreates the state the outcome needed (signed in, a cart filled).
    public static List<Journey> Mine(Hunt h, AppTwin? twin)
    {
        var found = new Dictionary<string, Journey>();
        var steps = h.Steps.Where(s => s.Ok && s.Blocked is null && s.Driver != "Probe" && Replayable.Contains(s.Action.Type)).ToList();
        for (var i = 0; i < steps.Count; i++)
            foreach (var req in steps[i].Requests)
            {
                var m = RequestLine().Match(req);
                if (!m.Success || !int.TryParse(m.Groups[3].Value, out var status)) continue;
                var outcome = $"{m.Groups[1].Value} {Probes.FoldIds(m.Groups[2].Value)}";
                if (!IsOutcome(m.Groups[1].Value, m.Groups[2].Value, status) || found.ContainsKey(outcome)) continue;
                var features = twin?.Features.Where(f => f.Endpoints.Contains(outcome)).ToList() ?? [];
                var stories = features.SelectMany(f => f.Stories).Concat(twin?.Rules.Where(r => r.On == outcome && r.Story is not null).Select(r => r.Story!) ?? []).Distinct().ToList();
                found[outcome] = new Journey
                {
                    Id = Slug($"{h.Role ?? "default"}-{outcome}"), ProjectId = h.ProjectId ?? "", Role = h.Role ?? "default", Outcome = outcome, OutcomeStatus = status,
                    Name = features.FirstOrDefault()?.Name ?? $"Reach {outcome}", Steps = steps.Take(i + 1).ToList(), FromHunt = h.Id,
                    Features = features.Select(f => f.Name).ToList(), Stories = stories,
                };
            }
        return [.. found.Values];
    }

    // The twin's features that write something and that no journey reaches yet: the gaps exploration should close.
    public static List<Journey> Planned(Project p, AppTwin? twin, IEnumerable<Journey> journeys)
    {
        if (twin is null) return [];
        var reached = journeys.Where(j => j.Source == "mined").Select(j => j.Outcome).ToHashSet();
        return twin.Features.SelectMany(f => f.Endpoints.Where(e => e.Split(' ')[0] is "POST" or "PUT" or "PATCH" or "DELETE" && !NotOutcome().IsMatch(e)).Select(e => (f, e)))
            .Where(x => !reached.Contains(x.e)).GroupBy(x => x.e).Select(g => g.First())
            .Select(x => new Journey
            {
                Id = Slug($"planned-{x.e}"), ProjectId = p.Id, Name = x.f.Name, Role = x.f.ForRoles.FirstOrDefault() ?? "", Source = "planned", Status = "planned",
                Outcome = x.e, Features = [x.f.Name], Stories = x.f.Stories,
            }).ToList();
    }

    public static string Slug(string s) => Regex.Replace(s.ToLowerInvariant().Replace("{id}", "id"), "[^a-z0-9]+", "-").Trim('-');

    [GeneratedRegex(@"^(GET|POST|PUT|PATCH|DELETE) (\S+) → (\d+)")] private static partial Regex RequestLine();
    [GeneratedRegex(@"/(login|logout|register|test/|admin/)", RegexOptions.IgnoreCase)] private static partial Regex NotOutcome();
}

// Per story: what checks it today, from the project's knowledge, the twin and the journeys.
public record StoryCoverage(string Key, string Title, int Version, List<string> Features, List<string> Rules, List<string> ProvenJourneys, List<string> OtherJourneys, bool Covered);

public static class Coverage
{
    public static List<StoryCoverage> Stories(KnowledgeSnapshot? k, AppTwin? twin, List<Journey> journeys) =>
        (k?.Stories ?? []).Select(s =>
        {
            var features = twin?.Features.Where(f => f.Stories.Contains(s.Key)).Select(f => f.Name).ToList() ?? [];
            // Only rules a reviewer accepted count: an AI draft no one has looked at checks nothing yet.
            var rules = twin?.Rules.Where(r => r.Story == s.Key && r.Status == "accepted").Select(r => r.Statement).ToList() ?? [];
            var mine = journeys.Where(j => j.Stories.Contains(s.Key) || j.Features.Intersect(features).Any()).ToList();
            var proven = mine.Where(j => j.Status == "proven").Select(j => j.Name).ToList();
            return new StoryCoverage(s.Key, s.Title, s.Version, features, rules, proven, mine.Where(j => j.Status != "proven").Select(j => $"{j.Name} ({j.Status})").ToList(),
                proven.Count > 0 || rules.Count > 0);
        }).ToList();
}
