namespace BugHunter.Api.Hunter;

// Replays a hunt's recorded steps in a fresh browser session and reports which checks fire, so a finding counts as
// confirmed only when it happens again without the model. Elements are found again by role and name (refs are
// per-page), and probe steps resend the same call as recorded in the new session.
public partial class Replayer(BrowserWorkerClient worker)
{
    public record Result(Dictionary<string, int> Fired, int Failed, int Ran, List<string> Problems)
    {
        public List<NetworkEvent> Network { get; init; } = [];
    }

    static readonly string[] NeedsTarget = ["click", "fill", "select", "check"];

    // Derives isolated credential values (RFC 5233 email plus-addressing) so replays run in distinct account partitions.
    public static Dictionary<string, string> IsolateSecrets(Dictionary<string, string> secrets, string tag)
    {
        var result = new Dictionary<string, string>(secrets);
        foreach (var (k, v) in secrets)
        {
            if (string.IsNullOrWhiteSpace(v)) continue;
            var at = v.IndexOf('@');
            if (at > 0 && v.IndexOf('.', at) > at)
            {
                var user = v[..at];
                var domain = v[at..];
                var plus = user.IndexOf('+');
                var baseUser = plus > 0 ? user[..plus] : user;
                result[k] = $"{baseUser}+{tag}{domain}";
            }
        }
        return result;
    }

    public async Task<Result> RunAsync(Hunt h, IReadOnlyList<HuntStep> steps, Dictionary<string, string> secrets, IReadOnlySet<string>? stopWhen, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromMinutes(3));
        var token = limit.Token;
        var (session, page) = await worker.OpenAsync(h.Url, [], secrets, token, fast: true);
        try
        {
            var st = new OracleState();
            var fired = new Dictionary<string, int>();
            var history = new List<NetworkEvent>();
            var rules = h.Invariants.Select(r => new Invariant { Id = r.Id, On = r.On, Check = r.Check }).ToList();
            var counts = new Dictionary<string, HashSet<int>>();
            var predicted = new Twin.PredictionState();

            void Observe(PageSnapshot snap, int n, bool dropAuth = false)
            {
                if (h.Predictions is { } ps)
                    foreach (var e in snap.Network)
                        foreach (var hit in Twin.Predictions.Check(ps, predicted, e, dropAuth)) fired.TryAdd(hit.Key, n);
                foreach (var hit in Oracles.Evaluate(st, snap)) fired.TryAdd(hit.Key, n);
                foreach (var e in snap.Network.Where(e => e.ResourceType is "xhr" or "fetch"))
                {
                    foreach (var r in rules.Where(r => InvariantRules.Applies(r, e)))
                        if (InvariantRules.EvaluateWith(r, e, InvariantRules.Prior(history, r, e)).Holds == false) fired.TryAdd($"invariant:{r.Id}", n);
                    history.Add(e);
                    // The same search in another letter case returning a different number of results.
                    if (e.Method == "GET" && e.Status is >= 200 and < 300 && Probes.Count(e) is int c && Query(e.Url) is { } q)
                    {
                        var key = $"{e.Path}?{q.ToLowerInvariant()}";
                        (counts.TryGetValue(key, out var set) ? set : counts[key] = []).Add(c);
                        if (counts[key].Count > 1) fired.TryAdd("search-case-sensitive", n);
                    }
                }
            }

            Observe(page, 0);
            var failed = 0; var ran = 0; var problems = new List<string>();
            void Fail(HuntStep s, string why) { failed++; if (problems.Count < 12) problems.Add($"step {s.N}: {why}"); }
            foreach (var s in steps.Where(s => s.N > 0))
            {
                if (stopWhen is { Count: > 0 } && stopWhen.All(fired.ContainsKey)) break;
                token.ThrowIfCancellationRequested();
                ran++;
                var a = s.Action;
                if (s.Driver == "Probe")
                {
                    var endpoint = $"{a.Key} {a.Args?.GetValueOrDefault("path")}";
                    var baseCall = history.LastOrDefault(e => e.Rid is not null && Probes.Endpoint(e) == Probes.Endpoint(new NetworkEvent { Method = a.Key ?? "", Url = "http://replay" + a.Args?.GetValueOrDefault("path") }));
                    if (baseCall is null) { Fail(s, $"the app never sent {endpoint}, so it could not be resent"); continue; }
                    try
                    {
                        var drop = a.Args?.GetValueOrDefault("dropAuth") == "true";
                        var e = await worker.RequestAsync(session, baseCall.Rid!.Value, a.Url, a.Value, drop, token, a.Key != baseCall.Method ? a.Key : null);
                        Observe(new PageSnapshot { Url = page.Url, Network = [e] }, s.N, drop);
                    }
                    catch (BrowserWorkerException e) { Fail(s, e.Message); }
                    continue;
                }
                var act = new HuntAction { Type = a.Type, Value = a.Value, Url = a.Url, Key = a.Key };
                if (s.Target is { } t && (NeedsTarget.Contains(a.Type) || a.Type == "press"))
                {
                    if (Resolve(page, t) is not { } el) { Fail(s, $"no {t.Role} \"{t.Name}\" on {page.Url}"); continue; }
                    act.Ref = el.Ref;
                }
                else if (NeedsTarget.Contains(a.Type)) { Fail(s, $"{a.Type} has no recorded element"); continue; }
                var res = await worker.ActAsync(session, act, true, token, screenshot: false);
                if (!res.Ok) Fail(s, res.Error ?? $"{a.Type} failed");
                page = res.Snapshot ?? await worker.SnapshotAsync(session, token, screenshot: false);
                Observe(page, s.N);
            }
            return new(fired, failed, ran, problems) { Network = history };
        }
        finally { await worker.CloseAsync(session); }
    }

    // The same element on the new page: same role and name (or label), at the same position among equals.
    public static ElementInfo? Resolve(PageSnapshot p, ElementInfo t)
    {
        var same = p.Elements.Where(e => e.Role == t.Role && e.Name == t.Name).ToList();
        if (same.Count == 0 && t.Label is { Length: > 0 } label) same = p.Elements.Where(e => e.Role == t.Role && e.Label == label).ToList();
        // Labels that carry live counts or amounts ("Cart (0)" then "Cart (2)", "Pay $97.19") are the same element.
        if (same.Count == 0 && Digits().IsMatch(t.Name)) same = p.Elements.Where(e => e.Role == t.Role && Shape(e.Name) == Shape(t.Name)).ToList();
        return same.Count == 0 ? null : same[Math.Min(t.Nth, same.Count - 1)];
    }

    static string Shape(string name) => Digits().Replace(name, "#");

    [System.Text.RegularExpressions.GeneratedRegex(@"\d+(?:[.,]\d+)*")] private static partial System.Text.RegularExpressions.Regex Digits();

    // Steps grouped into user actions: fills and selections belong with the click, key press or navigation that submits them,
    // so shrinking removes whole actions and needs far fewer replays.
    public static List<List<HuntStep>> Actions(IEnumerable<HuntStep> steps)
    {
        var units = new List<List<HuntStep>>(); var cur = new List<HuntStep>();
        foreach (var s in steps)
        {
            cur.Add(s);
            if (s.Action.Type is not ("fill" or "select" or "check")) { units.Add(cur); cur = []; }
        }
        if (cur.Count > 0) units.Add(cur);
        return units;
    }

    static string? Query(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
            ? u.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).Where(p => p.Length == 2 && p[0] is "q" or "query" or "search" or "term")
                .Select(p => Uri.UnescapeDataString(p[1].Replace('+', ' '))).FirstOrDefault()
            : null;

    // Delta debugging: drop chunks of steps while the finding still reproduces, within a replay budget.
    public static async Task<List<T>> ShrinkAsync<T>(List<T> steps, Func<List<T>, Task<bool>> reproduces, int budget)
    {
        var cur = steps; var n = 2;
        while (cur.Count >= 2 && budget > 0)
        {
            var chunk = (int)Math.Ceiling(cur.Count / (double)n); var reduced = false;
            for (var i = 0; i < n && budget > 0; i++)
            {
                var rest = cur.Where((_, j) => j / chunk != i).ToList();
                if (rest.Count == 0 || rest.Count == cur.Count) continue;
                budget--;
                if (await reproduces(rest)) { cur = rest; n = Math.Max(n - 1, 2); reduced = true; break; }
            }
            if (!reduced)
            {
                if (n >= cur.Count) break;
                n = Math.Min(n * 2, cur.Count);
            }
        }
        return cur;
    }
}
