using System.Text.Json;
using System.Text.Json.Nodes;
using BugHunter.Api.Common;
using BugHunter.Api.Hunter;
using BugHunter.Api.Projects;

namespace BugHunter.Api.Twin;

// The project's model of its application, versioned with every project hunt. Every item says how it is known (status)
// and from what (evidence), so nothing in it is taken on faith:
//   declared  from a specification (an OpenAPI document; P3)
//   observed  recorded by the engine in a hunt
//   inferred  proposed by a model (P4); never the basis of a finding until verified
//   verified  an inferred item the engine has since observed
//   accepted  confirmed by a reviewer (P8)
public class AppTwin
{
    public string ProjectId { get; set; } = "";
    public int Version { get; set; }
    public DateTime BuiltAt { get; set; } = DateTime.UtcNow;
    public string? FromHunt { get; set; }               // the hunt whose observations made this version
    public int? KnowledgeVersion { get; set; }          // the knowledge that hunt was grounded in
    public List<TwinState> States { get; set; } = [];
    public List<TwinRoad> Roads { get; set; } = [];
    public List<TwinEndpoint> Endpoints { get; set; } = [];
    public List<TwinUnknown> Unknowns { get; set; } = [];
    public List<string> Specs { get; set; } = [];       // specifications imported so far ("openapi http://.../openapi/v1.json")
    // What a model understood, each item checked against the layers above (Understanding.cs).
    public List<TwinEntity> Entities { get; set; } = [];
    public List<TwinFeature> Features { get; set; } = [];
    public List<TwinLifecycle> Lifecycles { get; set; } = [];
    public List<TwinRule> Rules { get; set; } = [];
    public List<TwinRejected> Rejected { get; set; } = [];      // the last understanding's proposals the verifier refused
    public TwinUnderstanding? Understanding { get; set; }
    public TwinChanges Changes { get; set; } = new();   // against the previous version
}

public record TwinEvidence(string Kind, string Ref, string? Note);

public abstract class TwinItem
{
    public string Status { get; set; } = "observed";
    public int FirstSeen { get; set; }                  // twin versions
    public int LastSeen { get; set; }
    public List<string> Roles { get; set; } = [];       // the project roles (or "no-session") it was seen as
    public List<TwinEvidence> Evidence { get; set; } = [];

    public void Saw(int version, string role, TwinEvidence evidence)
    {
        if (FirstSeen == 0) FirstSeen = version;
        LastSeen = version;
        if (!Roles.Contains(role)) Roles.Add(role);
        Evidence.RemoveAll(e => e.Ref == evidence.Ref);
        Evidence.Insert(0, evidence);
        if (Evidence.Count > 5) Evidence.RemoveRange(5, Evidence.Count - 5);
        if (Status == "inferred") Status = "verified";
        if (Status == "declared") Status = "observed";
    }
}

public class TwinState : TwinItem
{
    public string Id { get; set; } = "";
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public List<string> Landmarks { get; set; } = [];
}

public class TwinRoad : TwinItem
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Actions { get; set; } = "";
}

public class TwinEndpoint : TwinItem
{
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";               // ids folded: /api/bookings/{id}/cancel
    public Dictionary<string, List<int>> StatusesByRole { get; set; } = new();
    public List<string> RequestFields { get; set; } = [];
    public List<string> ResponseFields { get; set; } = [];
    public int Calls { get; set; }
    // What a specification says about it (empty when no specification covers it).
    public bool Declared { get; set; }
    public string? Summary { get; set; }
    public List<int> DeclaredStatuses { get; set; } = [];
    public List<string> DeclaredRequestFields { get; set; } = [];
    public List<string> DeclaredResponseFields { get; set; } = [];
    public string Key => $"{Method} {Path}";
}

public record TwinUnknown(string Kind, string Text);

public class TwinEntity : TwinItem
{
    public string Name { get; set; } = "";
    public List<string> Fields { get; set; } = [];
    public List<string> Endpoints { get; set; } = [];
}

public class TwinFeature : TwinItem
{
    public string Name { get; set; } = "";
    public List<string> Pages { get; set; } = [];
    public List<string> Endpoints { get; set; } = [];
    public List<string> Stories { get; set; } = [];
    public List<string> ForRoles { get; set; } = [];      // who the model says it is for (Roles: who it was seen as)
}

public record TwinTransition(string From, string To, string Endpoint, string? Role);

public class TwinLifecycle : TwinItem
{
    public string Entity { get; set; } = "";
    public string Field { get; set; } = "";
    public List<string> States { get; set; } = [];
    public List<TwinTransition> Transitions { get; set; } = [];
}

public class TwinRule : TwinItem
{
    public string Statement { get; set; } = "";
    public string? Story { get; set; }
    public string On { get; set; } = "";
    public string Check { get; set; } = "";
}

public record TwinRejected(string Kind, string Name, string Reason);

public class TwinUnderstanding
{
    public string Model { get; set; } = "";
    public DateTime At { get; set; } = DateTime.UtcNow;
    public int Calls { get; set; }
    public int TokensIn { get; set; }
    public int TokensOut { get; set; }
    public double CostUsd { get; set; }
    public double Seconds { get; set; }
    public string InputHash { get; set; } = "";              // what it was given; the same input is not asked again
    public int Proposed { get; set; }
    public int Admitted { get; set; }
}
public record PermissionRow(string Endpoint, Dictionary<string, string> Roles);

public class TwinChanges
{
    public List<string> EndpointsDeclared { get; set; } = [];  // newly described by an imported specification
    public List<string> Understood { get; set; } = [];         // "entity Booking", "rule HOTEL-3: ..." newly admitted
    public List<string> StatesAdded { get; set; } = [];
    public List<string> RoadsAdded { get; set; } = [];
    public List<string> EndpointsAdded { get; set; } = [];
    public List<string> FieldsAdded { get; set; } = [];        // "POST /api/bookings response.breakfastTotal"
    public List<string> StatusesAdded { get; set; } = [];      // "GET /api/staff/bookings 200 as guest"
    public bool Empty => StatesAdded.Count + RoadsAdded.Count + EndpointsAdded.Count + FieldsAdded.Count + StatusesAdded.Count + EndpointsDeclared.Count + Understood.Count == 0;
}

public static class TwinBuilder
{
    public const string NoSession = "no-session";

    // The next version: everything known so far, plus what this hunt observed, credited to its role.
    internal static AppTwin Copy(AppTwin? prev, Project project)
    {
        var t = prev is null ? new AppTwin() : JsonSerializer.Deserialize<AppTwin>(JsonSerializer.Serialize(prev, J.Opts), J.Opts)!;
        t.ProjectId = project.Id; t.Version = (prev?.Version ?? 0) + 1; t.BuiltAt = DateTime.UtcNow; t.FromHunt = null; t.Changes = new();
        return t;
    }

    public static AppTwin Next(AppTwin? prev, Project project, Hunt h, AppMap map, IEnumerable<(HuntStep Step, NetworkEvent Event)> history)
    {
        var t = Copy(prev, project);
        t.FromHunt = h.Id; t.KnowledgeVersion = h.KnowledgeVersion;
        var role = h.Role ?? "default";
        var v = t.Version;

        foreach (var id in map.StatesThisRun)
        {
            if (map.States.FirstOrDefault(s => s.Id == id) is not { } ms) continue;
            var s = t.States.FirstOrDefault(x => x.Id == id);
            if (s is null) { t.States.Add(s = new TwinState { Id = id }); t.Changes.StatesAdded.Add(id); }
            s.Url = ms.Url; s.Title = ms.Title; s.Landmarks = ms.Landmarks;
            s.Saw(v, role, new("hunt", h.Id, $"visited as {role}"));
        }

        foreach (var r in map.Roads.Where(r => map.RoadsThisRun.Contains($"{r.From}|{r.To}|{r.Signature}")))
        {
            var road = t.Roads.FirstOrDefault(x => x.From == r.From && x.To == r.To && x.Actions == r.Signature);
            if (road is null) { t.Roads.Add(road = new TwinRoad { From = r.From, To = r.To, Actions = r.Signature }); t.Changes.RoadsAdded.Add($"{r.From} → {r.To}"); }
            road.Saw(v, role, new("hunt", h.Id, $"driven as {role}"));
        }

        foreach (var (step, e) in history.Where(x => x.Event.ResourceType is "xhr" or "fetch" && x.Event.Status != 0))
            Observe(t, prev, e, step.Action.Args?.GetValueOrDefault("dropAuth") == "true" ? NoSession : role, new("api-call", $"{h.Id}:{step.N}", null));

        t.Unknowns = Unknowns(t, project);
        return t;
    }

    static void Observe(AppTwin t, AppTwin? prev, NetworkEvent e, string who, TwinEvidence evidence)
    {
        var key = Probes.Endpoint(e);
        var cut = key.IndexOf(' ');
        var ep = t.Endpoints.FirstOrDefault(x => x.Key == key);
        if (ep is null) { t.Endpoints.Add(ep = new TwinEndpoint { Method = key[..cut], Path = key[(cut + 1)..] }); t.Changes.EndpointsAdded.Add(key); }
        var wasObserved = prev?.Endpoints.Any(x => x.Key == key && x.Status != "declared") == true;
        if (!ep.StatusesByRole.TryGetValue(who, out var statuses)) ep.StatusesByRole[who] = statuses = [];
        if (!statuses.Contains(e.Status)) { statuses.Add(e.Status); statuses.Sort(); if (wasObserved) t.Changes.StatusesAdded.Add($"{key} {e.Status} as {who}"); }
        ep.Calls++;
        if (e.RequestJson() is JsonObject req) Merge(ep, ep.RequestFields, ApiCatalog.Fields(req, ""), "request", t.Changes, wasObserved);
        if (e.Status is >= 200 and < 300 && e.Json() is { } body) Merge(ep, ep.ResponseFields, ApiCatalog.Fields(body, ""), "response", t.Changes, wasObserved);
        ep.Saw(t.Version, who, evidence with { Note = $"HTTP {e.Status} as {who}" });
    }

    // A specification's endpoints as declared items: what the app says it offers, observed or not.
    public static AppTwin Declare(AppTwin? prev, Project project, List<DeclaredEndpoint> declared, string source)
    {
        var t = Copy(prev, project);
        foreach (var d in declared)
        {
            var ep = t.Endpoints.FirstOrDefault(x => x.Key == $"{d.Method} {d.Path}");
            if (ep is null) { t.Endpoints.Add(ep = new TwinEndpoint { Method = d.Method, Path = d.Path, Status = "declared", FirstSeen = t.Version, LastSeen = t.Version }); }
            if (!ep.Declared) t.Changes.EndpointsDeclared.Add(ep.Key);
            ep.Declared = true; ep.Summary = d.Summary; ep.DeclaredStatuses = d.Statuses; ep.DeclaredRequestFields = d.RequestFields; ep.DeclaredResponseFields = d.ResponseFields;
            ep.Evidence.RemoveAll(x => x.Kind == "spec");
            ep.Evidence.Add(new("spec", source, d.Summary));
        }
        if (!t.Specs.Contains(source)) t.Specs.Add(source);
        t.Unknowns = Unknowns(t, project);
        return t;
    }

    // Recorded traffic (a HAR file) as observations under a role of its own.
    public static AppTwin ObserveHar(AppTwin? prev, Project project, List<NetworkEvent> events, string source, string role = "har")
    {
        var t = Copy(prev, project);
        var n = 0;
        foreach (var e in events) Observe(t, prev, e, role, new("har", $"{source}#{++n}", null));
        t.Unknowns = Unknowns(t, project);
        return t;
    }

    // Endpoint x role: what each role got. Allowed: a success was seen; denied: only 401 or 403; error: only other
    // failures; unknown: never tried as that role.
    public static List<PermissionRow> Permissions(AppTwin t, Project p)
    {
        var roles = p.Roles.Select(r => r.Id).Append(NoSession).ToList();
        return t.Endpoints.OrderBy(e => e.Path).ThenBy(e => e.Method).Select(e => new PermissionRow(e.Key, roles.ToDictionary(r => r, r =>
            !e.StatusesByRole.TryGetValue(r, out var codes) || codes.Count == 0 ? "unknown"
            : codes.Any(c => c is >= 200 and < 300) ? "allowed" : codes.All(c => c is 401 or 403) ? "denied" : "error"))).ToList();
    }

    static void Merge(TwinEndpoint ep, List<string> into, IEnumerable<string> fields, string side, TwinChanges changes, bool known)
    {
        foreach (var f in fields)
            if (!into.Contains(f) && into.Count < 60) { into.Add(f); if (known) changes.FieldsAdded.Add($"{ep.Key} {side}.{f}"); }
    }

    // What the engine can say it does not know yet: the gaps a next hunt should close.
    static List<TwinUnknown> Unknowns(AppTwin t, Project p)
    {
        var u = new List<TwinUnknown>();
        foreach (var r in p.Roles.Where(r => t.States.All(s => !s.Roles.Contains(r.Id)) && t.Endpoints.All(e => !e.Roles.Contains(r.Id))))
            u.Add(new("role", $"Nothing has been observed as {r.Name} yet."));
        foreach (var e in t.Endpoints.Where(e => e.Status == "declared"))
            u.Add(new("coverage", $"{e.Key} is documented but no hunt has called it yet."));
        if (t.Specs.Count > 0)
            foreach (var e in t.Endpoints.Where(e => !e.Declared))
                u.Add(new("undocumented", $"{e.Key} is called by the app but not described by its specification."));
        foreach (var e in t.Endpoints.Where(e => e.Declared && e.Status != "declared"))
        {
            var extra = e.ResponseFields.Except(e.DeclaredResponseFields).Take(5).ToList();
            if (extra.Count > 0 && e.DeclaredResponseFields.Count > 0) u.Add(new("schema", $"{e.Key} returns {string.Join(", ", extra)}, which the specification does not describe."));
            var silent = e.StatusesByRole.Values.SelectMany(x => x).Any(c => c is >= 200 and < 300) ? e.DeclaredResponseFields.Except(e.ResponseFields).Take(5).ToList() : [];
            if (silent.Count > 0) u.Add(new("schema", $"{e.Key} is documented to return {string.Join(", ", silent)}, which no response has contained."));
        }
        foreach (var e in t.Endpoints.Where(e => e.Status != "declared" && e.StatusesByRole.Where(kv => kv.Key != NoSession).SelectMany(kv => kv.Value).All(s => s >= 400)))
            u.Add(new("endpoint", $"{e.Key} has never succeeded (statuses {string.Join(", ", e.StatusesByRole.SelectMany(kv => kv.Value).Distinct().Order())})."));
        foreach (var e in t.Endpoints.Where(e => e.Method != "GET" && e.Status != "declared" && !e.StatusesByRole.ContainsKey(NoSession)))
            u.Add(new("permission", $"{e.Key} has not been tried without a session."));
        return u;
    }
}
