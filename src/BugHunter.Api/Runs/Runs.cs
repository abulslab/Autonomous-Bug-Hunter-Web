using BugHunter.Api.Hunter;
using BugHunter.Api.Journeys;
using BugHunter.Api.Projects;
using BugHunter.Api.Twin;

namespace BugHunter.Api.Runs;

// A run: the project hunts started together (a survey of every role, or one hunt). When they have finished, the run's
// snapshot records what the project looked like, and comparing it with the baseline (the last accepted run) answers
// the first question of every QE review: what changed since the last run?
public class ProjectRun
{
    public string Id { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string Kind { get; set; } = "hunt";          // survey | hunt
    public List<string> Hunts { get; set; } = [];
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public RunSnapshot? Snapshot { get; set; }          // set once every hunt has finished
    public bool Accepted { get; set; }                  // this run is (or was) the baseline
    public DateTime? AcceptedAt { get; set; }
    public string? AcceptedBy { get; set; }
}

public class RunSnapshot
{
    public DateTime TakenAt { get; set; } = DateTime.UtcNow;
    public int KnowledgeVersion { get; set; }
    public int TwinVersion { get; set; }
    public Dictionary<string, KnownFinding> Findings { get; set; } = new();   // by finding key: the run's proven bugs
    public Dictionary<string, string> Journeys { get; set; } = new();         // journey id -> status after the run
    public Dictionary<string, string> Behaviour { get; set; } = new();        // endpoint -> fingerprint (statuses by role; response fields)
    public Dictionary<string, int> StoryVersions { get; set; } = new();
    public List<string> StoriesCovered { get; set; } = [];
    public List<string> Exercised { get; set; } = [];                           // "role METHOD /path" the run's hunts called
}

// Endpoint and Role: where the bug shows, so a later run counts it as fixed only if it tried that endpoint as that role.
public record KnownFinding(string Title, string Provenance, string? Reproduced, string Hunt, string Id, string? Endpoint = null, string? Role = null, string? Cause = null);

public class RunComparison
{
    public string? BaselineRun { get; set; }
    public List<KnownFinding> FindingsNew { get; set; } = [];
    public List<KnownFinding> FindingsFixed { get; set; } = [];
    public List<KnownFinding> FindingsStillOpen { get; set; } = [];
    public List<KnownFinding> FindingsNotRetested { get; set; } = []; // in the baseline, not seen now, but this run never tried where they show
    public List<string> JourneysRegressed { get; set; } = [];    // proven before, not now
    public List<string> JourneysRecovered { get; set; } = [];    // not proven before, proven now
    public List<string> JourneysNew { get; set; } = [];
    public List<BehaviourChange> BehaviourChanged { get; set; } = [];
    public List<string> EndpointsAdded { get; set; } = [];
    public List<string> EndpointsRemoved { get; set; } = [];
    public List<string> StoriesChanged { get; set; } = [];      // "HOTEL-3 v1 -> v2"
    public List<string> CoverageGained { get; set; } = [];
    public List<string> CoverageLost { get; set; } = [];
    // The baseline update policy: a run whose only changes are improvements (fixed findings, recovered or new journeys,
    // coverage gained, behaviour explained by a story or accepted) advances the baseline by itself; anything else waits
    // for a reviewer.
    // A run that left baseline bugs unretested covered less than the baseline: it never advances it by itself.
    public bool OnlyImprovements => FindingsNew.Count + JourneysRegressed.Count + EndpointsAdded.Count + EndpointsRemoved.Count + CoverageLost.Count + FindingsNotRetested.Count == 0
        && BehaviourChanged.All(b => !b.NeedsReview);
    public bool Blocking => FindingsNew.Count + JourneysRegressed.Count > 0;
    public bool Empty => FindingsNew.Count + FindingsFixed.Count + JourneysRegressed.Count + JourneysRecovered.Count + JourneysNew.Count
        + BehaviourChanged.Count + EndpointsAdded.Count + EndpointsRemoved.Count + StoriesChanged.Count + CoverageGained.Count + CoverageLost.Count == 0;
}

// ExplainedBy: the changed stories behind the endpoint (via the twin's features), if any. A change no story explains is
// a change signal, not a bug: principle 2, observed behaviour is never an expectation.
public record BehaviourChange(string Endpoint, string Before, string After, List<string> ExplainedBy, bool Accepted = false)
{
    public bool NeedsReview => ExplainedBy.Count == 0 && !Accepted;
}

public static class RunCompare
{
    // Suppressed: finding keys triaged as not a bug or won't fix (P9); they are not part of the run's findings.
    public static RunSnapshot Snapshot(IEnumerable<Hunt> hunts, AppTwin? twin, List<Journey> journeys, KnowledgeSnapshot? k, IReadOnlySet<string>? suppressed = null)
    {
        var s = new RunSnapshot { KnowledgeVersion = k?.Version ?? 0, TwinVersion = twin?.Version ?? 0 };
        foreach (var h in hunts)
        {
            var role = h.Role ?? "default";
            foreach (var f in h.Findings.Where(f => f.Provenance != "Judged" && f.Reproduced != "NotReproduced" && suppressed?.Contains(f.Key) != true))
                s.Findings.TryAdd(f.Key, new(f.Title, f.Provenance, f.Reproduced, h.Id, f.Id,
                    f.RequestMethod is { } m && f.RequestPath is { } p ? $"{m} {Probes.FoldIds(p.Split('?')[0])}" : null, role, f.RootCause));
            foreach (var ep in h.Coverage.Endpoints) if (!s.Exercised.Contains($"{role} {ep}")) s.Exercised.Add($"{role} {ep}");
        }
        foreach (var j in journeys.Where(j => j.Source == "mined")) s.Journeys[j.Id] = j.Status;
        foreach (var e in twin?.Endpoints.Where(e => e.Status != "declared") ?? [])
            s.Behaviour[e.Key] = $"{string.Join("; ", e.StatusesByRole.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {string.Join("/", kv.Value)}"))} | {string.Join(",", e.ResponseFields.Order())}";
        foreach (var st in k?.Stories ?? []) s.StoryVersions[st.Key] = st.Version;
        s.StoriesCovered = Coverage.Stories(k, twin, journeys).Where(c => c.Covered).Select(c => c.Key).ToList();
        return s;
    }

    // Accepted: endpoint -> behaviour a reviewer accepted as intended (P8); such a change is no longer a signal.
    // The release candidate: every run since the baseline, oldest first, as one snapshot. A bug any of them proved is
    // open; journeys, behaviour and coverage are as the latest run left them; what was exercised adds up.
    public static RunSnapshot Merge(IReadOnlyList<RunSnapshot> runs)
    {
        if (runs.Count == 1) return runs[0];
        var m = new RunSnapshot { TakenAt = runs[^1].TakenAt, KnowledgeVersion = runs.Max(r => r.KnowledgeVersion), TwinVersion = runs.Max(r => r.TwinVersion) };
        foreach (var r in runs)
        {
            foreach (var (k, f) in r.Findings) m.Findings[k] = f;
            foreach (var (k, j) in r.Journeys) m.Journeys[k] = j;
            foreach (var (k, b) in r.Behaviour) m.Behaviour[k] = b;
            foreach (var (k, v) in r.StoryVersions) m.StoryVersions[k] = v;
            foreach (var x in r.Exercised) if (!m.Exercised.Contains(x)) m.Exercised.Add(x);
        }
        m.StoriesCovered = runs[^1].StoriesCovered;
        return m;
    }

    public static RunComparison Compare(RunSnapshot? baseline, RunSnapshot current, AppTwin? twin, string? baselineRun, IReadOnlyDictionary<string, string>? accepted = null)
    {
        var c = new RunComparison { BaselineRun = baselineRun };
        if (baseline is null) return c;
        c.FindingsNew = current.Findings.Where(kv => !baseline.Findings.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();
        // Gone is not fixed unless this run tried again where the bug shows: a guest-only hunt says nothing about the desk.
        bool Retested(KnownFinding f) => current.Exercised.Count == 0 // a snapshot from before this was recorded
            || (f.Endpoint is null ? current.Exercised.Any(x => x.StartsWith((f.Role ?? "default") + " ")) : current.Exercised.Contains($"{f.Role ?? "default"} {f.Endpoint}"));
        var gone = baseline.Findings.Where(kv => !current.Findings.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();
        c.FindingsFixed = gone.Where(Retested).ToList();
        c.FindingsNotRetested = gone.Where(f => !Retested(f)).ToList();
        c.FindingsStillOpen = current.Findings.Where(kv => baseline.Findings.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();
        foreach (var (id, now) in current.Journeys)
        {
            if (!baseline.Journeys.TryGetValue(id, out var before)) { c.JourneysNew.Add(id); continue; }
            if (before == "proven" && now != "proven") c.JourneysRegressed.Add($"{id}: {now}");
            if (before != "proven" && now == "proven") c.JourneysRecovered.Add(id);
        }
        c.StoriesChanged = current.StoryVersions.Where(kv => baseline.StoryVersions.TryGetValue(kv.Key, out var v) && v != kv.Value).Select(kv => $"{kv.Key} v{baseline.StoryVersions[kv.Key]} -> v{kv.Value}")
            .Concat(current.StoryVersions.Keys.Except(baseline.StoryVersions.Keys).Select(k => $"{k} added")).ToList();
        var changedStories = current.StoryVersions.Where(kv => !baseline.StoryVersions.TryGetValue(kv.Key, out var v) || v != kv.Value).Select(kv => kv.Key).ToHashSet();
        foreach (var (ep, after) in current.Behaviour)
        {
            if (!baseline.Behaviour.TryGetValue(ep, out var before)) { c.EndpointsAdded.Add(ep); continue; }
            if (before == after) continue;
            var stories = twin?.Features.Where(f => f.Endpoints.Contains(ep)).SelectMany(f => f.Stories).Concat(twin.Rules.Where(r => r.On == ep && r.Story is not null).Select(r => r.Story!))
                .Distinct().Where(changedStories.Contains).ToList() ?? [];
            c.BehaviourChanged.Add(new(ep, before, after, stories, accepted?.GetValueOrDefault(ep) == after));
        }
        c.EndpointsRemoved = baseline.Behaviour.Keys.Except(current.Behaviour.Keys).ToList();
        c.CoverageGained = current.StoriesCovered.Except(baseline.StoriesCovered).ToList();
        c.CoverageLost = baseline.StoriesCovered.Except(current.StoriesCovered).ToList();
        return c;
    }
}
