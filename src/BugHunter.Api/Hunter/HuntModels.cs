using System.Text.Json.Nodes;

namespace BugHunter.Api.Hunter;

// A Bug Hunter run, stored as one JSON document ("hunter"/"hunt"). Secret values are never stored, only their names.
public class Hunt
{
    public string Id { get; set; } = "";
    public string Url { get; set; } = "";
    public string Mission { get; set; } = "";
    public string Status { get; set; } = "Queued"; // Queued | Running | Completed | Failed | Cancelled | Interrupted
    public string Strategy { get; set; } = "Hybrid"; // requested: Guided | Hybrid | Model | Heuristic
    public string Policy { get; set; } = "Model";    // what actually drove it: Hybrid | Model | Heuristic
    public int? SweepSteps { get; set; }             // Hybrid: steps the heuristic sweep took before the model
    public bool HasVideo { get; set; }                // a WebM recording of the browser session was saved
    public string? Connection { get; set; }
    public HuntModelInfo? Model { get; set; }        // the driver: decides each step
    public string? PlannerConnection { get; set; }
    public HuntModelInfo? PlannerModel { get; set; } // the planner: strategy, rules, probe ideas (null: the driver does it)
    public double? MaxCostUsd { get; set; }          // AI budget; past it the hunt finishes without AI
    public int MaxSteps { get; set; } = 40;
    public bool AllowIrreversible { get; set; }
    public List<string> SecretNames { get; set; } = [];
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? Error { get; set; }
    public List<string> Notes { get; set; } = [];
    public List<PlanItem> Plan { get; set; } = []; // the model's own test plan, carried from turn to turn
    public List<HuntRequirement> Requirements { get; set; } = []; // stories whose acceptance criteria the hunt checks
    public List<Invariant> Invariants { get; set; } = [];         // rules checked against every recorded API response
    public AppModel? AppModel { get; set; }                       // Guided: what the strategist understood about the app
    public List<Destination> Destinations { get; set; } = [];     // earlier Guided hunts: where the navigator went
    public List<Intent> Intents { get; set; } = [];               // Guided: what users come to do, and how each fared
    public string? Grounding { get; set; }                        // product notes from the user, given to the planner
    public string? ProjectId { get; set; }                        // the project the hunt belongs to, if any
    public int? KnowledgeVersion { get; set; }                    // the project's knowledge snapshot it was grounded in
    public int? TwinVersion { get; set; }                         // the app twin version this hunt produced
    public List<string> JourneysProven { get; set; } = [];        // journeys this hunt mined or replayed, with their result
    public Twin.PredictionSet? Predictions { get; set; }          // what the project's twin predicts, checked on every response
    public string? Environment { get; set; }                      // the project environment hunted
    public string? Role { get; set; }                             // the project role whose test account it used
    public List<string> ForcedSkillPacks { get; set; } = [];      // skill packs the project always applies
    public List<string> SkillPacks { get; set; } = [];            // skill packs that matched the application
    public int? MapStates { get; set; }
    public int? MapRoads { get; set; }
    public List<HuntStep> Steps { get; set; } = [];
    public List<Finding> Findings { get; set; } = [];
    public HuntCoverage Coverage { get; set; } = new();
    public HuntUsage Usage { get; set; } = new();
    public HuntBenchmark? Benchmark { get; set; }
    public bool Probe { get; set; } = true;     // run the engine's API probe phase on recorded calls
    public bool Confirm { get; set; } = true;   // replay findings in fresh sessions and shrink their repros
    public bool Shrink { get; set; } = true;    // shrink confirmed repros (off in CI, where only confirmation matters)
    public string? SetupUrl { get; set; }       // POSTed before the hunt and before every replay, to start from a known state
    public string? SetupSecret { get; set; }    // name of the secret sent as its bearer token
    public bool DynamicUsers { get; set; } = false; // when enabled, isolates test accounts per replay to avoid cross-run state pollution
    public int ProbeRequests { get; set; }      // requests the probe phase sent
}

public record HuntModelInfo(string Provider, string Model, string ExecutionMode);
public record PlanItem(string Idea, string Status); // Status: pending | done | dropped

public class HuntStep
{
    public int N { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Observation { get; set; }
    public string? Reasoning { get; set; }
    public HuntAction Action { get; set; } = new();
    public ElementInfo? Target { get; set; }
    public bool Ok { get; set; } = true;
    public string? Error { get; set; }
    public string? Blocked { get; set; }
    public List<string> FindingIds { get; set; } = [];
    public List<string> Requests { get; set; } = [];
    public bool HasShot { get; set; }
    public string? Driver { get; set; }  // Heuristic | Model | Routine: who chose this step
    public string? Routine { get; set; } // the routine this step belongs to
}

public class HuntAction
{
    public string Type { get; set; } = "";
    public int? Ref { get; set; }
    public string? Value { get; set; }
    public string? Url { get; set; }
    public string? Key { get; set; }
    public string? Routine { get; set; }                    // type "routine": which routine to run
    public Dictionary<string, string>? Args { get; set; }   // its arguments; elements named by label or name
}

public class ElementInfo
{
    public int Ref { get; set; }
    public string Role { get; set; } = "";
    public string Name { get; set; } = "";
    public string Tag { get; set; } = "";
    public string? Type { get; set; }
    public string? Value { get; set; }
    public string? Label { get; set; }
    public string? Placeholder { get; set; }
    public string? Href { get; set; }
    public string? Testid { get; set; }
    public bool? Disabled { get; set; }
    public bool? Checked { get; set; }
    public bool? Required { get; set; }
    public string? Invalid { get; set; } // the browser's own validation message (shown as a native bubble, not in the page text)
    public string? Min { get; set; }
    public string? Max { get; set; }
    public List<string>? Options { get; set; }
    public int? Form { get; set; }
    // Position among elements sharing role+name in the snapshot, so replay locators stay unambiguous.
    public int Nth { get; set; }
    public int Dupes { get; set; } = 1;

    public ElementInfo Clone() => (ElementInfo)MemberwiseClone();
}

public class Finding
{
    public string Id { get; set; } = "";
    public string Key { get; set; } = "";
    public string Oracle { get; set; } = "";
    public string Title { get; set; } = "";
    public string Severity { get; set; } = "Medium"; // Critical | High | Medium | Low
    // Observed: a built-in oracle fired on recorded evidence. Invariant: recorded evidence broke a rule the model (or user) wrote,
    // so the evidence is real but the rule itself may be wrong. Judged: the model's opinion, needs triage.
    public string Provenance { get; set; } = "Observed";
    public string? Rule { get; set; }        // Invariant findings: the check that failed
    public string? Requirement { get; set; } // the story whose acceptance criteria this breaks
    public List<string> ConfirmedBy { get; set; } = []; // later steps where the same check fired again, e.g. "Probe step 56"
    // Replay in fresh browser sessions without the model: Confirmed (every replay), Flaky (some), NotReproduced (none).
    public string? Reproduced { get; set; }
    public int ReplayPasses { get; set; }
    public int ReplayRuns { get; set; }
    public List<int>? MinimalSteps { get; set; } // the fewest recorded steps that still reproduce it
    public string? IssueUrl { get; set; }         // the GitHub issue it was filed as
    public int TrailSteps { get; set; }          // steps up to the finding before shrinking
    public int Step { get; set; }
    public string Url { get; set; } = "";
    public string Expected { get; set; } = "";
    public string Actual { get; set; } = "";
    public string? Evidence { get; set; }
    public string? RequestMethod { get; set; }
    public string? RequestPath { get; set; }
    public string? AssertText { get; set; }
    public List<string> Repro { get; set; } = [];
    public string? Spec { get; set; }
    public string? Cause { get; set; }           // root cause shared by findings that are one bug (a role reaching a protected area); defaults to Key
    public string RootCause => Cause ?? Key;
}

public class HuntCoverage
{
    public List<string> Pages { get; set; } = [];
    public int Actions { get; set; }
    public int FormsSubmitted { get; set; }
    public int Requests { get; set; }
    public List<string> Endpoints { get; set; } = [];
}

public class HuntUsage
{
    // Spend per finding that reproduced on every replay: the number that justifies (or not) the AI cost.
    public double? CostPerConfirmedBug { get; set; }
    public int ModelCalls { get; set; }
    public int TokensIn { get; set; }
    public int TokensOut { get; set; }
    public double CostUsd { get; set; }
    public int Repairs { get; set; }
    public int HeuristicFallbacks { get; set; }
}

public class HuntBenchmark
{
    public string Target { get; set; } = "";
    public List<SeededDefect> Seeded { get; set; } = [];
    public int Detected => Seeded.Count(s => s.FindingId is not null);
    public int Total => Seeded.Count;
    public int ConfirmedDetected { get; set; } // seeded defects whose finding reproduced on every replay
    public int ObservedFindings { get; set; }
    public int UnmatchedObserved { get; set; }
    public int InvariantFindings { get; set; }
    public int UnmatchedInvariant { get; set; }        // rule findings no seeded defect explains: false alarms when defects are off
    public int UnmatchedInvariantFlagged { get; set; } // of those, rules the engine itself marked as probably wrong
    // Seeded defect -> the rule finding whose violating response actually exhibited it; findings explained that way.
    public Dictionary<string, string> InvariantCredits { get; set; } = new();
    public HashSet<string> ExplainedInvariant { get; set; } = [];
    // Seeded defect -> the twin-mismatch finding whose response showed it; and prediction findings no defect explains.
    public Dictionary<string, string> PredictionCredits { get; set; } = new();
    public HashSet<string> ExplainedPrediction { get; set; } = [];
    public int PredictionFindings { get; set; }
    public int UnmatchedPrediction { get; set; }
}

public record SeededDefect(string Id, string Title, string? FindingId, string? Via = null); // Via: oracle | rule

// What the browser worker returns after open / act / snapshot.
public class PageSnapshot
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public List<string> Headings { get; set; } = [];
    public List<ElementInfo> Elements { get; set; } = [];
    public string Text { get; set; } = "";
    public string? Screenshot { get; set; }
    public List<ConsoleEvent> Console { get; set; } = [];
    public List<NetworkEvent> Network { get; set; } = [];
    public List<DialogEvent> Dialogs { get; set; } = [];
    public List<string> Blocked { get; set; } = [];
    public string? Error { get; set; }
}

public record ConsoleEvent(string Kind, string Text, string Url);
public record DialogEvent(string Type, string Message);

public class NetworkEvent
{
    public string Method { get; set; } = "";
    public string Url { get; set; } = "";
    public int Status { get; set; }
    public string ResourceType { get; set; } = "";
    public string? RequestBody { get; set; }
    public string? Body { get; set; }
    public string? Failed { get; set; }
    public int? Rid { get; set; } // the worker's id for this request, so the engine can resend a changed copy

    public string Path => Uri.TryCreate(Url, UriKind.Absolute, out var u) ? u.AbsolutePath : Url;
    public JsonNode? Json() { try { return Body is null ? null : JsonNode.Parse(Body); } catch { return null; } }
    public JsonNode? RequestJson() { try { return RequestBody is null ? null : JsonNode.Parse(RequestBody); } catch { return null; } }
}

public class ActResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public ElementInfo? Target { get; set; }
    public PageSnapshot? Snapshot { get; set; }
}
