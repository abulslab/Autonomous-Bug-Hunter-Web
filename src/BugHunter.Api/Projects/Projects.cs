using System.Text.Json;
using System.Text.Json.Nodes;
using BugHunter.Api.Common;
using BugHunter.Api.Storage;

namespace BugHunter.Api.Projects;

// A project is an application under test that Bug Hunter lives with across runs: where it runs, who can use it, what it
// should do (its versioned knowledge), and the hunts made against it.
public class Project
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Mission { get; set; }
    public List<ProjectEnvironment> Environments { get; set; } = [];
    public List<ProjectRole> Roles { get; set; } = [];
    public KnowledgeConfig Knowledge { get; set; } = new();
    public List<string> SkillPacks { get; set; } = [];       // always applied, on top of the ones matched to the app
    public List<string> OutOfScope { get; set; } = [];       // areas the planner is told to leave alone
    public List<SpecSource> Specs { get; set; } = [];        // API specifications to import into the twin
    public int KnowledgeVersion { get; set; }                // the latest knowledge snapshot; 0 before the first sync
    public int TwinVersion { get; set; }                     // the latest version of the app twin; 0 before the first hunt
    public string? BaselineRun { get; set; }                 // the accepted run later runs are compared with
    public Review.ReviewSettings Review { get; set; } = new(); // the review gate and its optional LLM judge (P8)
    public List<string> RetiredRules { get; set; } = [];       // "POST /api/x total == ..." rules a reviewer rejected; never proposed again
    public Dictionary<string, string> AcceptedBehaviour { get; set; } = new(); // endpoint -> behaviour accepted as intended
    public Learning.ChangeImpact? PendingChange { get; set; }  // what changed since the last change-driven hunt (P9)
    public Dictionary<string, Learning.TriageRecord> Triage { get; set; } = new(); // finding key -> outcome
    public Workflow.WorkflowConfig Workflow { get; set; } = new();  // where bugs are filed and reports published (P10)
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class ProjectEnvironment
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string? ResetUrl { get; set; }     // POSTed before each hunt and replay to reset test data
    public string? ResetSecret { get; set; }  // the project secret sent as its bearer token
}

// A role is a kind of user and its test account; the account's values are project secrets, referenced by name.
public class ProjectRole
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public Dictionary<string, string> Secrets { get; set; } = new(); // hunt secret name -> project secret name
    public List<string> Exclusive { get; set; } = [];                // path prefixes only this role may use ("/api/staff/")
}

// Kind "openapi": an OpenAPI 3 JSON document at Url (often the app's own /openapi/v1.json) or Path.
public class SpecSource
{
    public string Kind { get; set; } = "openapi";
    public string? Url { get; set; }
    public string? Path { get; set; }
}

public class KnowledgeConfig
{
    public SourceConfig Stories { get; set; } = new();
    public SourceConfig Documents { get; set; } = new();
}

// Kind: "files" (Path), "jira" (BaseUrl, Key = project key) or "confluence" (BaseUrl, Key = space key). Email and
// TokenSecret are the service account: Atlassian Cloud authenticates with an email and an API token.
public class SourceConfig
{
    public string Kind { get; set; } = "none";
    public string? Path { get; set; }
    public string? BaseUrl { get; set; }
    public string? Key { get; set; }
    public string? Email { get; set; }
    public string? TokenSecret { get; set; }
}

public class ProjectStore(IDocStore st)
{
    const string S = "projects";

    public async Task<Project?> GetAsync(string id, CancellationToken ct) => await st.GetAsync(S, "project", id, ct) is { } o ? o.Deserialize<Project>(J.Opts) : null;
    public async Task<List<Project>> ListAsync(CancellationToken ct) => (await st.ListAsync(S, "project", ct)).Select(o => o.Deserialize<Project>(J.Opts)!).OrderBy(p => p.Name).ToList();
    public Task SaveAsync(Project p, CancellationToken ct) { p.UpdatedAt = DateTime.UtcNow; return st.PutAsync(S, "project", p.Id, Docs.Obj(p), ct); }
    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        await st.DeleteAsync(S, "project", id, ct);
        await st.DeleteAsync(S, "secrets", id, ct);
    }

    // Test-account values and integration tokens. Write-only through the API; they go to the browser worker and the
    // knowledge sources, never to a model or a response.
    public async Task<Dictionary<string, string>> SecretsAsync(string id, CancellationToken ct) =>
        (await st.GetAsync(S, "secrets", id, ct))?.Where(kv => kv.Value is JsonValue && !kv.Key.StartsWith('_')).ToDictionary(kv => kv.Key, kv => kv.Value!.ToString()) ?? []; // "_key" is the store's
    public async Task SetSecretsAsync(string id, Dictionary<string, string?> values, CancellationToken ct)
    {
        var all = await SecretsAsync(id, ct);
        foreach (var (k, v) in values) if (string.IsNullOrEmpty(v)) all.Remove(k); else all[k] = v;
        await st.PutAsync(S, "secrets", id, new JsonObject(all.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))), ct);
    }

    public async Task<KnowledgeSnapshot?> KnowledgeAsync(string id, int version, CancellationToken ct) =>
        await st.GetAsync(S, "knowledge", $"{id}.{version:D4}", ct) is { } o ? o.Deserialize<KnowledgeSnapshot>(J.Opts) : null;
    public async Task<List<KnowledgeSnapshot>> KnowledgeHistoryAsync(string id, CancellationToken ct) =>
        (await st.ListAsync(S, "knowledge", ct)).Select(o => o.Deserialize<KnowledgeSnapshot>(J.Opts)!).Where(k => k.ProjectId == id).OrderByDescending(k => k.Version).ToList();
    public Task SaveKnowledgeAsync(KnowledgeSnapshot k, CancellationToken ct) => st.PutAsync(S, "knowledge", $"{k.ProjectId}.{k.Version:D4}", Docs.Obj(k), ct);

    public async Task<Twin.AppTwin?> TwinAsync(string id, int version, CancellationToken ct) =>
        await st.GetAsync(S, "twin", $"{id}.{version:D4}", ct) is { } o ? o.Deserialize<Twin.AppTwin>(J.Opts) : null;
    public async Task<List<Twin.AppTwin>> TwinHistoryAsync(string id, CancellationToken ct) =>
        (await st.ListAsync(S, "twin", ct)).Select(o => o.Deserialize<Twin.AppTwin>(J.Opts)!).Where(t => t.ProjectId == id).OrderByDescending(t => t.Version).ToList();
    public Task SaveTwinAsync(Twin.AppTwin t, CancellationToken ct) => st.PutAsync(S, "twin", $"{t.ProjectId}.{t.Version:D4}", Docs.Obj(t), ct);

    public async Task<List<Journeys.Journey>> JourneysAsync(string id, CancellationToken ct) =>
        (await st.ListAsync(S, "journey", ct)).Select(o => o.Deserialize<Journeys.Journey>(J.Opts)!).Where(j => j.ProjectId == id).OrderBy(j => j.Role).ThenBy(j => j.Name).ToList();
    public Task SaveJourneyAsync(Journeys.Journey j, CancellationToken ct) => st.PutAsync(S, "journey", $"{j.ProjectId}.{j.Id}", Docs.Obj(j), ct);

    public async Task<Runs.ProjectRun?> RunAsync(string id, string run, CancellationToken ct) =>
        await st.GetAsync(S, "run", $"{id}.{run}", ct) is { } o ? o.Deserialize<Runs.ProjectRun>(J.Opts) : null;
    public async Task<List<Runs.ProjectRun>> RunsAsync(string id, CancellationToken ct) =>
        (await st.ListAsync(S, "run", ct)).Select(o => o.Deserialize<Runs.ProjectRun>(J.Opts)!).Where(r => r.ProjectId == id).OrderByDescending(r => r.StartedAt).ToList();
    public Task SaveRunAsync(Runs.ProjectRun r, CancellationToken ct) => st.PutAsync(S, "run", $"{r.ProjectId}.{r.Id}", Docs.Obj(r), ct);

    public async Task<List<Review.ReviewItem>> ReviewAsync(string id, CancellationToken ct) =>
        (await st.ListAsync(S, "review", ct)).Select(o => o.Deserialize<Review.ReviewItem>(J.Opts)!).Where(i => i.ProjectId == id).OrderByDescending(i => i.CreatedAt).ToList();
    public Task SaveReviewAsync(Review.ReviewItem i, CancellationToken ct) => st.PutAsync(S, "review", $"{i.ProjectId}.{i.Id}", Docs.Obj(i), ct);
}

public class SimulatorOptions
{
    public string? Url { get; set; }                 // the Jira and Confluence simulators (src/Simulators)
    public string Email { get; set; } = "bughunter@example.test";
    public string? ApiToken { get; set; } = Environment.GetEnvironmentVariable("SIM_TOKEN"); // or Simulators:ApiToken
}
