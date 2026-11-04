using System.Text;
using System.Text.RegularExpressions;

namespace BugHunter.Api.Hunter;

// The app as a map: states (page type plus whether someone is signed in) and roads (recorded actions that move
// between them). Learned from every hunt and kept per site, so later hunts drive known routes instead of exploring
// again. The AI strategist annotates it with meaning (AppModel) and picks destinations; the navigator drives routes.
public partial class AppMap
{
    public string Site { get; set; } = "";
    public List<MapState> States { get; set; } = [];
    public List<MapRoad> Roads { get; set; } = [];
    public AppModel? Model { get; set; }
    public List<ApiEndpoint> Apis { get; set; } = [];      // the API as observed through the browser
    public List<Intent> Intents { get; set; } = [];        // what users come to do, from the last warm-up
    public int Hunts { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    // What this hunt saw (the map is loaded fresh for every hunt), so the project's twin can credit the right run and role.
    [System.Text.Json.Serialization.JsonIgnore] public HashSet<string> StatesThisRun { get; } = [];
    [System.Text.Json.Serialization.JsonIgnore] public HashSet<string> RoadsThisRun { get; } = [];

    const int MaxStates = 150, MaxRoads = 500;

    public static string SiteOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? $"{u.Scheme}://{u.Authority}{u.AbsolutePath.TrimEnd('/')}" : url;

    // A state is the page template (ids folded, so product/p-101 and product/p-102 are one place) plus sign-in.
    public static string StateOf(PageSnapshot p)
    {
        var page = Uri.TryCreate(p.Url, UriKind.Absolute, out var u) ? u.AbsolutePath + u.Fragment.Split('?')[0] : p.Url.Split('?')[0];
        // "/shop/" and "/shop/#/" are the same page of a hash-routed app.
        page = page.EndsWith("#/") ? page[..^2] : page.EndsWith('#') ? page[..^1] : page;
        return IdSegment().Replace(page, "/{id}") + (SignedIn(p) ? " [signed in]" : "");
    }

    public static bool SignedIn(PageSnapshot p) => p.Elements.Any(e => e.Role is "button" or "link" && SignOut().IsMatch(e.Name));

    public MapState Visit(PageSnapshot p)
    {
        var id = StateOf(p);
        var s = States.FirstOrDefault(x => x.Id == id);
        if (s is null)
        {
            if (States.Count >= MaxStates) States.Remove(States.MinBy(x => x.Visits)!);
            States.Add(s = new MapState { Id = id, Url = p.Url });
        }
        s.Visits++; s.Title = p.Title; s.Url = p.Url;
        StatesThisRun.Add(id);
        s.Landmarks = p.Elements.Where(e => e.Role is "button" or "link" or "textbox" or "searchbox" or "spinbutton" or "combobox")
            .Select(e => $"{e.Role} \"{(e.Name.Length > 0 ? e.Name : e.Label)}\"").Distinct().Take(25).ToList();
        return s;
    }

    // A road: the actions (fills plus the click that submits them) that took the app from one state to another.
    public void Learn(string from, IReadOnlyList<HuntStep> actions, string to)
    {
        if (from == to || actions.Count == 0 || actions.Any(a => !a.Ok || a.Blocked is not null)) return;
        if (actions.Any(a => a.Action.Type is "click" or "fill" or "select" or "check" or "press" && a.Target is null)) return;
        var sig = string.Join(" > ", actions.Select(a => ModelHuntPolicy.Describe(a.Action, a.Target)));
        RoadsThisRun.Add($"{from}|{to}|{sig}");
        var road = Roads.FirstOrDefault(r => r.From == from && r.To == to && r.Signature == sig);
        if (road is null)
        {
            if (Roads.Count >= MaxRoads) Roads.Remove(Roads.MinBy(r => r.Ok - r.Failed)!);
            Roads.Add(road = new MapRoad
            {
                From = from, To = to, Signature = sig,
                Actions = actions.Select(a => new MapAction { Action = new HuntAction { Type = a.Action.Type, Value = a.Action.Value, Url = a.Action.Url, Key = a.Action.Key }, Target = a.Target?.Clone() }).ToList()
            });
        }
        road.Ok++; road.LastAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    // Cheapest known route between two states: fewest actions, avoiding roads that failed before.
    public List<MapRoad>? Route(string from, string to)
    {
        if (from == to) return [];
        var dist = new Dictionary<string, double> { [from] = 0 };
        var via = new Dictionary<string, MapRoad>();
        var open = new PriorityQueue<string, double>();
        open.Enqueue(from, 0);
        while (open.TryDequeue(out var at, out var d))
        {
            if (at == to) break;
            if (d > dist.GetValueOrDefault(at, double.MaxValue)) continue;
            foreach (var r in Roads.Where(r => r.From == at && r.Ok > 0))
            {
                var cost = d + r.Actions.Count + 3.0 * r.Failed / (r.Ok + r.Failed);
                if (cost >= dist.GetValueOrDefault(r.To, double.MaxValue)) continue;
                dist[r.To] = cost; via[r.To] = r;
                open.Enqueue(r.To, cost);
            }
        }
        if (!via.ContainsKey(to)) return null;
        var path = new List<MapRoad>();
        for (var at = to; at != from; at = via[at].From) path.Insert(0, via[at]);
        return path;
    }

    // States reachable from here (for the strategist: only destinations the navigator can drive to).
    public HashSet<string> Reachable(string from)
    {
        var seen = new HashSet<string> { from };
        var queue = new Queue<string>([from]);
        while (queue.TryDequeue(out var at))
            foreach (var r in Roads.Where(r => r.From == at && r.Ok > 0 && seen.Add(r.To))) queue.Enqueue(r.To);
        return seen;
    }

    // The states the strategist may choose, numbered S1..Sn, most visited first, with how far each is from here.
    public string Numbered(IReadOnlyList<MapState> states, string from)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < states.Count; i++)
        {
            var s = states[i];
            sb.AppendLine($"- S{i + 1} = \"{s.Id}\" ({s.Title}){(Route(from, s.Id) is { } r ? $", {r.Sum(x => x.Actions.Count)} actions from here" : "")}");
            sb.AppendLine($"    has: {string.Join(", ", s.Landmarks.Take(14))}");
        }
        return sb.ToString();
    }

    // The model's answer to "which state": a number from the list, or a name copied exactly (any letter case).
    // Never a partial match: "/shop/" is part of every state's name, which once sent every destination to the home page.
    public static string? Match(string? answer, IReadOnlyList<string> listed)
    {
        var a = (answer ?? "").Trim().Trim('"');
        if (StateNumber().Match(a) is { Success: true } m && int.Parse(m.Groups[1].Value) is var n && n >= 1 && n <= listed.Count) return listed[n - 1];
        return listed.FirstOrDefault(s => s == a) ?? listed.FirstOrDefault(s => s.Equals(a, StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex(@"^S(\d+)\b", RegexOptions.IgnoreCase)] private static partial Regex StateNumber();

    public string Describe(string from, int maxStates)
    {
        var reach = Reachable(from);
        var sb = new StringBuilder();
        foreach (var s in States.Where(s => reach.Contains(s.Id)).OrderByDescending(s => s.Visits).Take(maxStates))
        {
            sb.AppendLine($"- STATE \"{s.Id}\" ({s.Title}){(Route(from, s.Id) is { } r ? $", {r.Sum(x => x.Actions.Count)} actions from here" : "")}");
            sb.AppendLine($"    has: {string.Join(", ", s.Landmarks.Take(14))}");
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"/(?:[A-Za-z]{1,4}-)?\d+(?=/|$)")] private static partial Regex IdSegment();
    [GeneratedRegex(@"sign ?out|log ?out", RegexOptions.IgnoreCase)] private static partial Regex SignOut();
}

public class MapState
{
    public string Id { get; set; } = "";
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public int Visits { get; set; }
    public List<string> Landmarks { get; set; } = [];
}

public class MapRoad
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Signature { get; set; } = "";
    public List<MapAction> Actions { get; set; } = [];
    public int Ok { get; set; }
    public int Failed { get; set; }
    public DateTime LastAt { get; set; } = DateTime.UtcNow;
}

public class MapAction
{
    public HuntAction Action { get; set; } = new();
    public ElementInfo? Target { get; set; }
}

// What the strategist understood about the app: its purpose, the things it manages, the journeys and the risks.
public class AppModel
{
    public string Domain { get; set; } = "";
    public List<string> Entities { get; set; } = [];
    public List<string> Workflows { get; set; } = [];
    public List<string> Risks { get; set; } = [];
}

// What a user comes to do (from warm-up): where on the map, what success looks like (checked as rules), and the risky
// variants to try. Tested most important first; its status comes from evidence, not from the model.
public class Intent : Destination
{
    public string Id { get; set; } = "";
    public string Actor { get; set; } = "";
    public int Priority { get; set; } = 3;
    public List<string> Preconditions { get; set; } = [];
    public List<string> Success { get; set; } = [];
    public List<string> RuleIds { get; set; } = [];
    public List<IntentVariant> Variants { get; set; } = [];
    // Held: its success checks were evaluated and held. Violated: one broke (a bug). Exercised: actions ran but no check
    // applied. Unreachable, Skipped (budget), Failed (nothing could run).
}

public class IntentVariant
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? Routine { get; set; }
    public Dictionary<string, string>? Args { get; set; }
    public string Status { get; set; } = "Planned"; // Planned | Tested | Failed | Unreachable | Skipped
    public int Steps { get; set; }
    public string? Problem { get; set; }
    public List<string> FindingIds { get; set; } = [];
}

// A place to test and what to do there; the navigator drives to State, then the test runs. (Intents extend it.)
public class Destination
{
    public string Goal { get; set; } = "";
    public string State { get; set; } = "";
    public string? Routine { get; set; }
    public Dictionary<string, string>? Args { get; set; }
    public string? Requirement { get; set; }
    public string Why { get; set; } = "";
    public string Status { get; set; } = "Planned"; // Planned | Reached | Unreachable | Tested | Failed | Skipped
    public int RouteSteps { get; set; }
    public int TestSteps { get; set; }
    public int Reroutes { get; set; }
    public List<string> FindingIds { get; set; } = [];
    public string? Problem { get; set; } // why the test could not run as planned
}
