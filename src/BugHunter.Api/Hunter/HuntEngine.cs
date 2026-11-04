using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using BugHunter.Api.Common;
using BugHunter.Api.Models;
using BugHunter.Api.Storage;

namespace BugHunter.Api.Hunter;

// Hunts live in the tenant-scoped simulator document store; screenshots are separate documents so the hunt stays small.
public class HuntStore(IDocStore st)
{
    const string S = "hunter";

    public Task SaveAsync(Hunt h, CancellationToken ct) => st.PutAsync(S, "hunt", h.Id, Docs.Obj(h), ct);
    public async Task<Hunt?> GetAsync(string id, CancellationToken ct) => await st.GetAsync(S, "hunt", id, ct) is { } o ? o.Deserialize<Hunt>(J.Opts) : null;
    public async Task<List<Hunt>> ListAsync(CancellationToken ct) =>
        (await st.ListAsync(S, "hunt", ct)).Select(o => o.Deserialize<Hunt>(J.Opts)!).OrderByDescending(h => h.CreatedAt).ToList();
    // One map per site, shared by every hunt of that site.
    public async Task<AppMap?> MapAsync(string site, CancellationToken ct) => await st.GetAsync(S, "map", site, ct) is { } o ? o.Deserialize<AppMap>(J.Opts) : null;
    public async Task<List<AppMap>> MapsAsync(CancellationToken ct) => (await st.ListAsync(S, "map", ct)).Select(o => o.Deserialize<AppMap>(J.Opts)!).ToList();
    public Task SaveMapAsync(AppMap map, CancellationToken ct) => st.PutAsync(S, "map", map.Site, Docs.Obj(map), ct);
    public Task SaveShotAsync(string id, int n, string base64, CancellationToken ct) => st.PutAsync(S, "shot", $"{id}:{n}", new JsonObject { ["jpeg"] = base64 }, ct);
    public async Task<byte[]?> ShotAsync(string id, int n, CancellationToken ct) =>
        await st.GetAsync(S, "shot", $"{id}:{n}", ct) is { } o && J.Text(o["jpeg"]) is { Length: > 0 } b ? Convert.FromBase64String(b) : null;
}

public partial class HuntEngine(HuntStore store, BrowserWorkerClient worker, ModelGateway gateway, DemoShopClient demo, HunterOptions opts, HuntRunner runner,
    IHttpClientFactory http, Projects.ProjectStore projects)
{
    // Which oracle keys count as detecting each seeded shop-simulator defect (benchmark scoring only; oracles never see this).
    static readonly Dictionary<string, string[]> SeededKeys = new()
    {
        ["coupon-stacking"] = ["duplicate-promotion", "discount-exceeds-subtotal"],
        ["expired-card"] = ["invalid-input:expired-card"],
        ["negative-qty"] = ["non-positive-qty"],
        ["search-case"] = ["search-miss", "search-case-sensitive"],
        ["no-stock-check"] = ["oversell"],
        ["tax-rounding"] = ["tax-rounding"],
    };
    // What a rule must check to be about a seeded defect: one of these fields, or (entries starting with "/") an endpoint
    // whose path contains it. A broken rule earns credit for a defect only if it is about it: a capacity rule broken by a
    // booking that also has past dates finds the capacity bug, not the date bug.
    static readonly Dictionary<string, string[]> DefiningFields = new()
    {
        ["staff-open"] = ["/staff/"],
        ["cancel-shipped"] = ["/cancel"],
        // Cross-endpoint defects: a rule checks one response, so it cannot be about them; only a consistency prediction can.
        ["listing-price-stale"] = [],
        ["gift-wrap-per-item"] = ["giftwrap"],
        ["desk-open"] = ["/staff/"],
        ["cancel-after-checkin"] = ["/cancel"],
        ["quote-mismatch"] = [],
        ["breakfast-once"] = ["breakfast"],
        ["coupon-stacking"] = ["discount", "coupons"],
        ["expired-card"] = ["expiry"],
        ["negative-qty"] = ["qty", "quantity"],
        ["search-case"] = ["items", "query.q"],
        ["no-stock-check"] = ["stock", "qty"],
        ["tax-rounding"] = ["tax"],
        ["past-checkin"] = ["checkin", "today"],
        ["checkout-before-checkin"] = ["checkout", "nights"],
        ["over-capacity"] = ["guests", "maxguests"],
        ["wrong-total"] = ["total"],
    };
    // The prediction kind that is about each seeded defect (benchmark scoring only).
    static readonly Dictionary<string, string> PredictionKinds = new()
    {
        ["quote-mismatch"] = "consistency", ["listing-price-stale"] = "consistency",
        ["cancel-after-checkin"] = "lifecycle", ["cancel-shipped"] = "lifecycle",
        ["desk-open"] = "permission", ["staff-open"] = "permission",
    };
    // Findings that are direct consequences of a seeded defect, so they are not counted as unexplained.
    static readonly Dictionary<string, string[]> Consequences = new()
    {
        ["negative-qty"] = ["negative-amount", "subtotal-mismatch", "total-mismatch"],
        ["coupon-stacking"] = ["negative-amount", "total-mismatch"],
    };

    public async Task RunAsync(Hunt h, Dictionary<string, string> secrets, Func<bool> userCancelled, CancellationToken ct)
    {
        using var activity = Telemetry.Source.StartActivity("hunt");
        activity?.SetTag("bughunter.hunt", h.Id); activity?.SetTag("bughunter.strategy", h.Strategy); activity?.SetTag("url.full", h.Url);
        h.Status = "Running"; h.StartedAt = DateTime.UtcNow;
        await store.SaveAsync(h, CancellationToken.None);
        string? session = null;
        using var keepAlive = new CancellationTokenSource();
        try
        {
            var (policy, guide) = await PolicyAsync(h, ct);
            var planner = policy switch { ModelHuntPolicy m => m, HybridHuntPolicy hp => hp.Model, _ => guide };
            var probed = new HashSet<string>(); // groups already sent, so a later probe phase only adds new ones
            var uiFailures = 0; var probedAfterSweep = false;
            h.Benchmark = await BenchmarkAsync(h, ct);
            if (h.ProjectId is not null) await LoadPredictionsAsync(h);
            if (h.SetupUrl is not null && !await SetupAsync(h, secrets, ct)) h.Notes.Add("The setup request before the hunt failed; the hunt started from the app's current state.");
            var (id, first) = await worker.OpenAsync(h.Url, [], secrets, ct);
            session = id;
            runner.Live[h.Id] = id;
            _ = KeepAliveAsync(id, keepAlive.Token);
            var oracle = new OracleState();
            oracle.Catalog.AddRange(catalog);
            var ctx = new HuntContext { Hunt = h, Page = first, Oracles = oracle };
            var map = await store.MapAsync(AppMap.SiteOf(h.Url), ct) ?? new AppMap { Site = AppMap.SiteOf(h.Url) };
            map.Hunts++; map.Visit(first);
            var knownStates = map.States.Count;
            var pending = new List<HuntStep>(); string? pendingFrom = null; // fills waiting for the action that submits them
            // Guided: a short survey first (shorter still when earlier hunts already mapped the site), then destinations.
            // Even with a known map, survey enough to call the app's write endpoints in this session (probes and checks need them).
            var limit = guide is null ? h.MaxSteps : Math.Min(h.MaxSteps, Math.Max(15, (int)(h.MaxSteps * (knownStates >= 8 ? 0.3 : 0.4))));
            if (guide is not null && knownStates > 1) h.Notes.Add($"The app map from {map.Hunts - 1} earlier hunt{(map.Hunts == 2 ? "" : "s")} already has {knownStates} states and {map.Roads.Count} roads; the survey is shorter.");
            var open = new HuntStep { N = 0, Url = first.Url, Title = first.Title, Action = new HuntAction { Type = "open", Url = h.Url } };
            h.Steps.Add(open);
            await ObserveAsync(h, open, first, oracle, ct);
            Backfill(h, oracle);

            HeuristicHuntPolicy? fallback = null;
            var failures = 0; var n = 0;
            var deadline = DateTime.UtcNow.AddMinutes(opts.MaxMinutes);
            var resumed = false;
        explore:
            while (n < limit)
            {
                ct.ThrowIfCancellationRequested();
                if (DateTime.UtcNow > deadline) { h.Notes.Add($"Stopped at the {opts.MaxMinutes}-minute time budget."); break; }
                ctx.Step = n + 1;
                Decision d; string driver;
                try
                {
                    d = await policy.NextAsync(ctx, ct); failures = 0;
                    driver = policy switch { HybridHuntPolicy hp => hp.LastDriver, HeuristicHuntPolicy => "Heuristic", _ => "Model" };
                }
                // A provider timeout surfaces as OperationCanceledException too; only a real cancel of this hunt stops it.
                catch (Exception e) when (policy is not HeuristicHuntPolicy && !ct.IsCancellationRequested)
                {
                    h.Usage.HeuristicFallbacks++;
                    var why = e is OperationCanceledException ? "timed out" : e.Message;
                    fallback ??= new HeuristicHuntPolicy();
                    if (e is AiBudgetExceededException)
                    {
                        h.Notes.Add($"{e.Message} The heuristic explorer, probes and checks finished the hunt without AI.");
                        policy = fallback; guide = null; planner = null;
                    }
                    else if (++failures >= 3)
                    {
                        // Keep hunting rather than fail: the heuristic explorer finishes the budget.
                        h.Notes.Add($"The model failed three times in a row, so the heuristic explorer drove the rest of the hunt. Last error: {why}");
                        policy = fallback;
                    }
                    else h.Notes.Add($"Step {n + 1}: the model call failed ({why}); the heuristic explorer chose this step.");
                    d = await fallback.NextAsync(ctx, ct);
                    driver = "Heuristic";
                }
                Backfill(h, oracle); // rules the model just wrote are checked against everything recorded so far
                // Hybrid: probe what the sweep recorded while its session is still valid, before the model explores (and maybe signs out).
                if (h.Probe && !probedAfterSweep && policy is HybridHuntPolicy { HandedOver: true })
                {
                    probedAfterSweep = true;
                    await RunProbesAsync(null);
                }
                if (d.Suspicion is { } s) AddJudged(h, s, ctx.Page.Url);
                if (d.Plan is { Count: > 0 } plan) h.Plan = plan;
                if (d.Done || d.Actions.Count == 0) { h.Notes.Add($"Hunter finished after {n} steps: {d.Reasoning}"); break; }

                await ExecuteAsync(d, driver);
            }

            // Runs one decision's actions on the live page, records them as steps, observes the result and learns the road.
            async Task ExecuteAsync(Decision d, string driver)
            {
                var planned = ctx.Page; ctx.LastError = null;
                HuntStep? lastStep = null; PageSnapshot? snap = null; var observed = false; var batch = new List<HuntStep>();
                for (var i = 0; i < d.Actions.Count && n < h.MaxSteps; i++)
                {
                    var a = d.Actions[i]; var last = i == d.Actions.Count - 1 || n + 1 == h.MaxSteps;
                    if (a.Type == "routine")
                    {
                        // A routine runs its own steps against the live page and ends the batch.
                        await RunRoutineAsync(a, i == 0 ? d : null);
                        observed = true;
                        break;
                    }
                    var target = a.Ref is int r ? Target(planned, r) : null;
                    // Smaller models "click" a text field with the value they mean to type.
                    if (a.Type == "click" && !string.IsNullOrEmpty(a.Value) && target?.Role is "textbox" or "searchbox" or "spinbutton") a.Type = "fill";
                    var step = new HuntStep { N = ++n, Url = planned.Url, Title = planned.Title, Action = a, Target = target, Observation = i == 0 ? d.Observation : null, Reasoning = i == 0 ? d.Reasoning : null, Driver = driver };
                    h.Steps.Add(step); lastStep = step; batch.Add(step);
                    if (a.Ref is int missing && target is null) { step.Ok = false; step.Error = $"There is no element [{missing}] on this page."; ctx.LastError = step.Error; break; }
                    if (!h.AllowIrreversible && target is not null && a.Type is "click" or "press" && Irreversible().IsMatch(target.Name))
                    {
                        step.Ok = false; step.Blocked = "Irreversible action; this hunt does not allow it without approval.";
                        ctx.LastError = $"'{target.Name}' is blocked for this hunt (irreversible). Explore something else.";
                        break;
                    }
                    if (SignOutBlocked(h, driver, a, target) is { } no)
                    {
                        step.Ok = false; step.Blocked = no; ctx.LastError = no;
                        break;
                    }
                    var res = await worker.ActAsync(session, a, last, ct);
                    // An earlier action in the batch re-rendered the page: find the same element again and retry once.
                    if (!res.Ok && target is not null && res.Error?.Contains("no longer on the page") == true)
                    {
                        var fresh = await worker.SnapshotAsync(session, ct);
                        await ObserveAsync(h, step, fresh, oracle, ct);
                        var same = fresh.Elements.Where(e => e.Role == target.Role && e.Name == target.Name).ToList();
                        if (same.ElementAtOrDefault(Math.Min(target.Nth, Math.Max(0, same.Count - 1))) is { } again)
                        {
                            a.Ref = again.Ref; planned = fresh;
                            res = await worker.ActAsync(session, a, last, ct);
                        }
                    }
                    step.Ok = res.Ok; step.Error = res.Error; step.Target ??= res.Target;
                    snap = res.Snapshot;
                    if (!res.Ok) { ctx.LastError = $"{ModelHuntPolicy.Describe(a, step.Target)} failed: {res.Error}"; break; }
                }
                // The UI keeps failing (locators that do not resolve, custom widgets): probe what the clicks that worked recorded.
                uiFailures = lastStep is { Ok: false, Blocked: null } ? uiFailures + 1 : 0;
                if (h.Probe && uiFailures >= 4)
                {
                    uiFailures = 0;
                    h.Notes.Add($"Step {n}: four UI actions in a row failed, so the engine probed the API calls recorded so far before exploring on.");
                    await RunProbesAsync(null);
                }
                if (observed) return;
                snap ??= await worker.SnapshotAsync(session, ct);
                await ObserveAsync(h, lastStep!, snap, oracle, ct);
                map.Learn(AppMap.StateOf(planned), batch, AppMap.StateOf(snap)); map.Visit(snap);
                ctx.Page = snap;
            }

            // Executes one action as a step (routines and the navigator), observes it, and learns roads: fills wait for the
            // action that submits them, then the group becomes one road from the state before to the state after.
            async Task<(bool Ok, string? Error, PageSnapshot Page)> StepAsync(HuntStep step, CancellationToken token)
            {
                h.Steps.Add(step);
                var before = AppMap.StateOf(ctx.Page);
                if (!h.AllowIrreversible && step.Target is { } t && step.Action.Type is "click" or "press" && Irreversible().IsMatch(t.Name))
                {
                    step.Ok = false; step.Blocked = "Irreversible action; this hunt does not allow it without approval.";
                    pending.Clear();
                    return (false, step.Blocked, ctx.Page);
                }
                if (SignOutBlocked(h, step.Driver, step.Action, step.Target) is { } no)
                {
                    step.Ok = false; step.Blocked = no; pending.Clear();
                    return (false, no, ctx.Page);
                }
                var res = await worker.ActAsync(session, step.Action, true, token);
                step.Ok = res.Ok; step.Error = res.Error;
                var page = res.Snapshot ?? await worker.SnapshotAsync(session, token);
                await ObserveAsync(h, step, page, oracle, token);
                if (step.Action.Type is "fill" or "select" or "check" && res.Ok) { pendingFrom ??= before; pending.Add(step); }
                else { map.Learn(pendingFrom ?? before, [.. pending, step], AppMap.StateOf(page)); pending.Clear(); pendingFrom = null; }
                map.Visit(page);
                ctx.Page = page;
                return (res.Ok, res.Error, page);
            }

            // Navigator: drive the cheapest known route to a state, rerouting from wherever a road actually led.
            async Task<bool> NavigateAsync(Destination dest)
            {
                var first = true;
                for (var attempt = 0; attempt < 4 && n < h.MaxSteps; attempt++)
                {
                    var here = AppMap.StateOf(ctx.Page);
                    if (here == dest.State) return true;
                    var route = map.Route(here, dest.State);
                    if (route is null)
                    {
                        // No road from here: start again from the hunt's first page, where the survey's roads begin.
                        if (attempt > 0 && ctx.Page.Url == h.Url) return false;
                        var home = new HuntStep { N = ++n, Url = ctx.Page.Url, Title = ctx.Page.Title, Driver = "Navigator", Action = new HuntAction { Type = "navigate", Url = h.Url }, Reasoning = $"No known road to {dest.State} from {here}; starting from the first page." };
                        dest.RouteSteps++;
                        await StepAsync(home, ct);
                        continue;
                    }
                    var off = false;
                    foreach (var road in route)
                    {
                        foreach (var ma in road.Actions)
                        {
                            if (n >= h.MaxSteps) return false;
                            var a = new HuntAction { Type = ma.Action.Type, Value = ma.Action.Value, Url = ma.Action.Url, Key = ma.Action.Key };
                            ElementInfo? t = null;
                            if (ma.Target is { } mt)
                            {
                                t = Replayer.Resolve(ctx.Page, mt);
                                if (t is null) { off = true; break; }
                                a.Ref = t.Ref;
                            }
                            var step = new HuntStep { N = ++n, Url = ctx.Page.Url, Title = ctx.Page.Title, Action = a, Target = t is null ? null : Target(ctx.Page, t.Ref), Driver = "Navigator",
                                Reasoning = first ? $"Driving to {dest.State} for: {dest.Goal}" : null };
                            first = false;
                            dest.RouteSteps++;
                            if (!(await StepAsync(step, ct)).Ok) { off = true; break; }
                        }
                        if (off || AppMap.StateOf(ctx.Page) != road.To) { road.Failed++; dest.Reroutes++; off = true; break; }
                    }
                    if (!off && AppMap.StateOf(ctx.Page) == dest.State) return true;
                }
                return AppMap.StateOf(ctx.Page) == dest.State;
            }

            // Guided phase. Warm-up: the survey's map, the API it observed, matching skill packs, stories and product notes go to
            // the planner, which writes the app model and its intents. Then each intent, most important first: drive there over
            // known roads, achieve it, try its risky variants. Its outcome comes from its success checks, not from the model.
            async Task GuidedAsync(ModelHuntPolicy guide)
            {
                ApiCatalog.Learn(map, oracle.History.Select(x => x.Event));
                var corpus = string.Join(" ", map.States.SelectMany(s => s.Landmarks.Append(s.Title).Append(s.Id))) + " " + string.Join(" ", map.Apis.Select(a => a.Endpoint))
                    + " " + h.Mission + " " + string.Join(" ", h.Requirements.Select(r => $"{r.Title} {r.Criteria}"));
                var all = SkillPacks.LoadAll(opts);
                // Packs the project always applies come first; keyword-matched ones follow.
                var packs = all.Where(p => h.ForcedSkillPacks.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
                    .Concat(SkillPacks.Select(all, corpus)).DistinctBy(p => p.Name).ToList();
                h.SkillPacks = packs.Select(p => p.Name).ToList();
                h.Notes.Add($"Warm-up: the map has {map.States.Count} states and {map.Roads.Count} roads; {map.Apis.Count} API endpoints observed; skill packs: {(packs.Count == 0 ? "none matched" : string.Join(", ", packs.Select(p => p.Title)))}.");

                List<Intent> intents;
                try { intents = await guide.PlanIntentsAsync(ctx, map, packs, ct); }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    h.Notes.Add($"The planner could not model the application ({(e is OperationCanceledException ? "timed out" : e.Message)}).");
                    return;
                }
                h.Intents = intents;
                map.Intents = intents.Select(i => new Intent { Id = i.Id, Goal = i.Goal, State = i.State, Actor = i.Actor, Priority = i.Priority, Success = i.Success, Requirement = i.Requirement }).ToList();
                Backfill(h, oracle); // the success checks are judged on everything recorded so far, too
                await store.SaveAsync(h, ct);

                foreach (var intent in intents.OrderBy(i => i.Priority))
                {
                    if (n >= h.MaxSteps) { intent.Status = "Skipped"; foreach (var v in intent.Variants) v.Status = "Skipped"; continue; }
                    var findings = h.Findings.Count; var start = n;
                    if (!await NavigateAsync(intent)) { intent.Status = "Unreachable"; await store.SaveAsync(h, ct); continue; }
                    // Achieve it first, with ordinary data: if even this fails, that is the first thing to know.
                    await LastMileAsync(guide, $"Achieve this as {(intent.Actor.Length > 0 ? intent.Actor : "a user")}: {intent.Goal}. Success means: {string.Join("; ", intent.Success)}", 2);
                    foreach (var v in intent.Variants)
                    {
                        if (n >= h.MaxSteps) { v.Status = "Skipped"; continue; }
                        if (AppMap.StateOf(ctx.Page) != intent.State && !await NavigateAsync(intent)) { v.Status = "Unreachable"; continue; }
                        var vFindings = h.Findings.Count; var vStart = n;
                        if (v.Routine is not null && Routines.Get(v.Routine) is not null)
                        {
                            ctx.LastError = null;
                            await RunRoutineAsync(new HuntAction { Type = "routine", Routine = v.Routine, Args = v.Args }, new Decision($"Variant of \"{intent.Goal}\".", v.Name, [], null, false));
                            if (n == vStart || ctx.LastError is not null) v.Problem = ctx.LastError ?? $"Routine {v.Routine} found nothing to do with those arguments.";
                        }
                        // No routine, or it did not fit the page: the model tries the variant itself, briefly.
                        if (v.Routine is null || Routines.Get(v.Routine) is null || v.Problem is not null)
                            await LastMileAsync(guide, $"Variant of \"{intent.Goal}\": {v.Name}{(v.Problem is null ? "" : $" (the planned routine did not fit: {v.Problem})")}", 1);
                        v.Steps = n - vStart;
                        v.Status = v.Steps > 0 ? "Tested" : "Failed";
                        v.FindingIds = h.Findings.Skip(vFindings).Select(f => f.Id).ToList();
                    }
                    intent.TestSteps = n - start - intent.RouteSteps;
                    intent.FindingIds = h.Findings.Skip(findings).Select(f => f.Id).ToList();
                    var rules = h.Invariants.Where(r => r.Intent == intent.Id).ToList();
                    intent.Status = rules.Any(r => r.Status == "Violated") ? "Violated" : rules.Any(r => r.Status == "Holding") ? "Held" : intent.TestSteps > 0 ? "Exercised" : "Failed";
                    await store.SaveAsync(h, ct);
                }
                h.Notes.Add($"Intents: {intents.Count(i => i.Status == "Held")} held, {intents.Count(i => i.Status == "Violated")} violated, {intents.Count(i => i.Status == "Exercised")} exercised without a check, "
                    + $"{intents.Count(i => i.Status is "Unreachable" or "Failed")} not reached or not runnable, {intents.Count(i => i.Status == "Skipped")} skipped for budget.");
            }

            // The model works on one task on the current page for a few turns (the "last mile" the navigator cannot drive).
            async Task LastMileAsync(ModelHuntPolicy model, string focus, int turns)
            {
                ctx.Focus = focus;
                for (var turn = 0; turn < turns && n < h.MaxSteps; turn++)
                {
                    ctx.Step = n + 1;
                    Decision d;
                    try { d = await model.NextAsync(ctx, ct); }
                    catch (AiBudgetExceededException e) { h.Notes.Add(e.Message + " The remaining intents ran without the model."); break; }
                    catch (Exception e) when (!ct.IsCancellationRequested) { h.Notes.Add($"\"{focus[..Math.Min(60, focus.Length)]}\": the model call failed ({(e is OperationCanceledException ? "timed out" : e.Message)})."); break; }
                    if (d.Suspicion is { } s) AddJudged(h, s, ctx.Page.Url);
                    if (d.Done || d.Actions.Count == 0) break;
                    await ExecuteAsync(d, "Model");
                }
                ctx.Focus = null;
            }

            // Probe phase: resend recorded API calls with changes and judge every response with the same oracles and rules.
            async Task RunProbesAsync(ModelHuntPolicy? model)
            {
                var plan = Probes.Plan(h, oracle);
                if (h.Predictions is { } ps)
                {
                    var bases = Probes.Bases(oracle);
                    var twin = h.ProjectId is not null && await projects.GetAsync(h.ProjectId, CancellationToken.None) is { TwinVersion: > 0 } proj ? await projects.TwinAsync(proj.Id, proj.TwinVersion, CancellationToken.None) : null;
                    // Probes that write (another item, a lifecycle transition) only where irreversible actions are allowed.
                    var writes = h.AllowIrreversible ? Twin.Predictions.OtherItemProbes(oracle.Predicted, bases).Concat(Twin.Predictions.LifecycleProbes(ps, oracle.Predicted, bases)) : [];
                    plan.InsertRange(0, writes.Concat(Twin.Predictions.CrossRoleProbes(ps, twin, bases)));
                }
                if (model is not null)
                {
                    try { plan.AddRange(await model.PlanProbesAsync(ctx, ct)); }
                    catch (Exception e) when (!ct.IsCancellationRequested) { h.Notes.Add($"The model could not plan probes ({(e is OperationCanceledException ? "timed out" : e.Message)}); the engine's own probes ran."); }
                }
                var sent = 0; var groups = 0; var refused = 0; var lost = false;
                foreach (var grp in plan.GroupBy(a => a.Group + "|" + Probes.Endpoint(a.Base) + "|" + a.Label).Select(x => x.First()).GroupBy(a => a.Group))
                {
                    var key = string.Join(";", grp.Select(a => $"{Probes.Endpoint(a.Base)} {a.Kind} {a.Label}"));
                    if (lost || !probed.Add(key) || h.ProbeRequests >= Probes.MaxRequests) continue;
                    groups++;
                    var counts = new Dictionary<string, int>();
                    foreach (var a in grp)
                    {
                        if (a.Setup is { } setup) await SendAsync(setup, grp.Key);
                        for (var t = 0; t < a.Times && h.ProbeRequests < Probes.MaxRequests; t++)
                        {
                            var (step, e) = await SendAsync(a, grp.Key);
                            sent++;
                            if (e is null) continue;
                            // Calls that worked with a session now refused: the session has ended, so further probes would prove nothing.
                            if (!a.DropAuth && a.Base.Status is >= 200 and < 300 && e.Status is 401 or 403) { if (++refused >= 3) lost = true; }
                            else if (!a.DropAuth) refused = 0;
                            if (a.Kind == "case" && Probes.Count(e) is int c) counts[a.Label] = c;
                            // A call that changes data accepted with no session. It may be public by design, so it needs triage.
                            if (a.DropAuth && e.Status is >= 200 and < 300)
                                AddJudged(h, new Suspicion($"{a.Base.Method} {a.Base.Path} works without a session",
                                    "A request that changes data is refused (401/403) when nobody is signed in, unless it is public by design.",
                                    $"Sent without cookies or session headers, it returned HTTP {e.Status}.", "High"), step.Url);
                        }
                    }
                    if (counts.Count >= 2 && counts.Values.Distinct().Count() > 1)
                        AddFinding(h, oracle, new Oracles.Hit("search-case-sensitive", "case-variants", "Results depend on letter case", "High",
                            $"{grp.First().Base.Method} {grp.First().Base.Path} returns the same results in any letter case.",
                            string.Join(", ", counts.Select(c => $"{c.Key} → {c.Value} results")) + "."), h.Steps[^1], ctx.Page.Url);
                }
                if (lost) h.Notes.Add("Probe phase stopped: calls that had worked were refused (401/403), so the app's session had ended (for example, the hunt signed out). The remaining probes were skipped rather than reported as passed.");
                if (sent > 0) h.Notes.Add($"Probe phase: {sent} API requests in {groups} probes on {plan.Select(a => Probes.Endpoint(a.Base)).Distinct().Count()} endpoints, judged by the same oracles and rules.");
                await store.SaveAsync(h, ct);
            }

            async Task<(HuntStep Step, NetworkEvent? Event)> SendAsync(Probe a, string group)
            {
                var step = new HuntStep
                {
                    N = ++n, Url = ctx.Page.Url, Title = ctx.Page.Title, Driver = "Probe", Routine = a.Kind,
                    Action = new HuntAction
                    {
                        Type = "request", Key = a.Method ?? a.Base.Method, Url = a.Url ?? a.Base.Url, Value = a.Body, Routine = a.Kind,
                        Args = new() { ["label"] = a.Label, ["group"] = group, ["base"] = a.BaseStep.ToString(), ["path"] = a.Base.Path, ["source"] = a.Source, ["dropAuth"] = a.DropAuth ? "true" : "false" }
                    },
                    Reasoning = a.Why
                };
                h.Steps.Add(step);
                h.ProbeRequests++;
                Telemetry.ProbeRequests.Add(1, new System.Diagnostics.TagList { { "kind", a.Kind } });
                try
                {
                    var e = await worker.RequestAsync(session, a.Base.Rid!.Value, a.Url, a.Body, a.DropAuth, ct, a.Method);
                    await ObserveAsync(h, step, new PageSnapshot { Url = ctx.Page.Url, Title = ctx.Page.Title, Network = [e] }, oracle, ct);
                    return (step, e);
                }
                catch (BrowserWorkerException e) { step.Ok = false; step.Error = e.Message; return (step, null); }
            }

            async Task RunRoutineAsync(HuntAction call, Decision? why)
            {
                var routine = Routines.Get(call.Routine);
                var first = true; var start = n;
                if (routine is null) { ctx.LastError = $"There is no routine named '{call.Routine}'."; return; }
                var r = new RoutineContext
                {
                    Page = ctx.Page,
                    // A routine may use at most 12 steps, so one call cannot spend the whole budget.
                    StepsLeft = () => Math.Min(h.MaxSteps, start + 12) - n,
                    Report = hit => AddFinding(h, oracle, hit, h.Steps[^1], ctx.Page.Url),
                    Do = async (a, target, token) =>
                    {
                        var step = new HuntStep { N = ++n, Url = ctx.Page.Url, Title = ctx.Page.Title, Action = a, Target = target is null ? null : Target(ctx.Page, target.Ref) ?? target,
                            Driver = "Routine", Routine = routine.Name, Observation = first ? why?.Observation : null, Reasoning = first ? $"Routine {routine.Name}: {why?.Reasoning}" : null };
                        first = false;
                        return await StepAsync(step, token);
                    }
                };
                await routine.RunAsync(r, call.Args ?? [], ct);
                if (r.Problems.Count > 0) ctx.LastError = $"Routine {routine.Name}: {string.Join(" ", r.Problems.Take(3))}";
            }
            if (guide is not null && !resumed)
            {
                h.SweepSteps = n;
                h.Notes.Add($"Survey covered steps 1-{n}: the map now has {map.States.Count} states and {map.Roads.Count} roads.");
                if (h.Probe) await RunProbesAsync(null); // while the survey's session is still valid
                await GuidedAsync(guide);
                if (h.Intents.Count > 0)
                    h.Notes.Add($"Guided: driving between intents took {h.Intents.Sum(x => x.RouteSteps)} steps; testing them took {h.Intents.Sum(x => x.TestSteps)}.");
                // Never leave budget unused: when the destinations are done (or the strategist failed), the model explores on.
                if (n < h.MaxSteps)
                {
                    resumed = true;
                    h.Notes.Add($"{h.MaxSteps - n} steps were left after the guided phase, so the model explored on from step {n + 1}.");
                    policy = guide; limit = h.MaxSteps;
                    goto explore;
                }
            }
            h.MapStates = map.States.Count; h.MapRoads = map.Roads.Count;
            try { map.UpdatedAt = DateTime.UtcNow; await store.SaveMapAsync(map, ct); } catch (IOException) { /* the map is an optimisation */ }
            if (n >= h.MaxSteps) h.Notes.Add($"Used the full budget of {h.MaxSteps} steps.");
            // Probe phase after exploration, with the model's own probe ideas when a model drives the hunt.
            if (h.Probe) await RunProbesAsync(planner);
            if (h.Confirm) await ConfirmAsync(h, secrets, ct);
            if (h.ProjectId is not null) { await UpdateTwinAsync(h, map, oracle); await JourneysAsync(h, secrets, ct); }
            h.Status = "Completed";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            h.Status = userCancelled() ? "Cancelled" : "Interrupted";
        }
        catch (Exception e)
        {
            h.Status = "Failed"; h.Error = e is OperationCanceledException ? "A browser or model call timed out." : e.Message;
        }
        finally
        {
            runner.Live.TryRemove(h.Id, out _);
            keepAlive.Cancel();
            if (session is not null)
            {
                await worker.CloseAsync(session);
                h.HasVideo = await worker.SaveVideoAsync(session, HuntRunner.VideoPath(opts, h.Id), CancellationToken.None);
            }
            Finish(h);
            h.FinishedAt = DateTime.UtcNow;
            await store.SaveAsync(h, CancellationToken.None);
            Record(h, activity);
        }
    }

    // Hunt outcome as metrics and span attributes: what an operator watches (cost per confirmed bug, false-positive signals).
    static void Record(Hunt h, System.Diagnostics.Activity? activity)
    {
        Telemetry.Hunts.Add(1, new System.Diagnostics.TagList { { "status", h.Status }, { "strategy", h.Strategy } });
        if (h.StartedAt is { } s) Telemetry.HuntMinutes.Record(((h.FinishedAt ?? DateTime.UtcNow) - s).TotalMinutes, new System.Diagnostics.TagList { { "strategy", h.Strategy } });
        foreach (var f in h.Findings) Telemetry.Findings.Add(1, new System.Diagnostics.TagList { { "provenance", f.Provenance }, { "reproduced", f.Reproduced ?? "n/a" } });
        activity?.SetTag("bughunter.status", h.Status);
        activity?.SetTag("bughunter.findings.confirmed", h.Findings.Count(f => f.Reproduced == "Confirmed"));
        activity?.SetTag("bughunter.cost_usd", h.Usage.CostUsd);
    }

    // Phase 3: replay every proven finding twice in fresh browser sessions, with no model, then shrink each confirmed
    // finding's repro to the fewest recorded steps that still reproduce it.
    // Re-runs confirmation for a finished hunt (the secrets are supplied again; they are never stored).
    public async Task ReconfirmAsync(Hunt h, Dictionary<string, string> secrets, CancellationToken ct)
    {
        foreach (var f in h.Findings) { f.Reproduced = null; f.ReplayPasses = f.ReplayRuns = 0; f.MinimalSteps = null; }
        h.Notes.RemoveAll(n => n.StartsWith("Confirmation") || n.StartsWith("Shrinking"));
        var status = h.Status;
        try { await ConfirmAsync(h, secrets, ct); }
        finally { h.Status = status; Finish(h); await store.SaveAsync(h, CancellationToken.None); }
    }

    async Task ConfirmAsync(Hunt h, Dictionary<string, string> secrets, CancellationToken ct)
    {
        var targets = h.Findings.Where(f => f.Provenance != "Judged").ToList();
        if (targets.Count == 0) return;
        h.Status = "Confirming";
        await store.SaveAsync(h, ct);
        using var confirmSpan = Telemetry.Source.StartActivity("hunt.confirm");
        var replayer = new Replayer(worker);
        var trail = h.Steps.Where(s => s.N <= targets.Max(f => f.Step) && s.Ok && s.Blocked is null).ToList();
        var keys = targets.Select(f => f.Key).ToHashSet();
        var runs = new List<Replayer.Result>();
        var isolatedCount = 0;
        for (var i = 0; i < 2; i++)
        {
            var runSecrets = (h.DynamicUsers && h.SetupUrl is null)
                ? Replayer.IsolateSecrets(secrets, $"replay{i + 1}_{h.Id[..Math.Min(6, h.Id.Length)]}")
                : secrets;
            if (runSecrets != secrets) isolatedCount++;
            try { await SetupAsync(h, secrets, ct); runs.Add(await replayer.RunAsync(h, trail, runSecrets, keys, ct)); }
            catch (Exception e) when (e is BrowserWorkerException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                h.Notes.Add($"Confirmation replay {i + 1} failed: {(e is OperationCanceledException ? "it took longer than 3 minutes" : e.Message)}.");
            }
        }
        foreach (var f in targets)
        {
            f.ReplayRuns = 2;
            f.ReplayPasses = runs.Count(r => r.Fired.ContainsKey(f.Key));
            f.Reproduced = f.ReplayPasses == 2 ? "Confirmed" : f.ReplayPasses == 1 ? "Flaky" : "NotReproduced";
        }
        var confirmed = targets.Where(f => f.Reproduced == "Confirmed").ToList();
        h.Notes.Add($"Confirmation: each finding was replayed twice in fresh browser sessions with no model. {confirmed.Count} of {targets.Count} reproduced both times"
            + $"{(targets.Count(f => f.Reproduced == "Flaky") is > 0 and var fl ? $", {fl} once (flaky)" : "")}{(targets.Count(f => f.Reproduced == "NotReproduced") is > 0 and var nr ? $", {nr} not at all" : "")}."
            + (runs.Count > 0 && runs.Average(r => r.Failed) > 0 ? $" About {runs.Average(r => r.Failed):0} replayed steps could not be repeated, for example: {string.Join("; ", runs.MaxBy(r => r.Problems.Count)!.Problems.Take(4))}." : "")
            + (h.SetupUrl is not null ? " Each replay started after the setup request."
               : isolatedCount > 0 ? " No setup request configured; replays used dynamic account partitioning (RFC 5233 sub-addressing) to prevent cross-run state pollution."
               : " No setup request was configured, so each replay started from whatever state the previous run left."));
        await store.SaveAsync(h, ct);

        // Shrink: most severe first, within a time budget; each attempt is a full replay that stops once the finding fires.
        if (!h.Shrink) return;
        var deadline = DateTime.UtcNow.AddMinutes(10);
        foreach (var f in confirmed.OrderBy(f => f.Severity switch { "Critical" => 0, "High" => 1, "Medium" => 2, _ => 3 }))
        {
            var mine = trail.Where(s => s.N <= f.Step).ToList();
            f.TrailSteps = mine.Count(s => s.N > 0);
            if (f.TrailSteps <= 3 || DateTime.UtcNow > deadline) continue;
            async Task<bool> Reproduces(List<HuntStep> subset)
            {
                if (DateTime.UtcNow > deadline) return false;
                var shrinkSecrets = (h.DynamicUsers && h.SetupUrl is null)
                    ? Replayer.IsolateSecrets(secrets, $"shrink_{h.Id[..Math.Min(6, h.Id.Length)]}")
                    : secrets;
                try { await SetupAsync(h, secrets, ct); return (await replayer.RunAsync(h, subset, shrinkSecrets, new HashSet<string> { f.Key }, ct)).Fired.ContainsKey(f.Key); }
                catch (Exception e) when (e is BrowserWorkerException or OperationCanceledException && !ct.IsCancellationRequested) { return false; }
            }
            // Findings at the same point often share a cause: start from another finding's shrunk path when it works here too.
            if (confirmed.FirstOrDefault(o => o.MinimalSteps is not null && o.Step <= f.Step && o.MinimalSteps.Count < mine.Count) is { } other)
            {
                var shared = mine.Where(s => s.N == 0 || other.MinimalSteps!.Contains(s.N) || s.N > other.Step).ToList();
                if (await Reproduces(shared)) mine = shared;
            }
            var units = await Replayer.ShrinkAsync(Replayer.Actions(mine), u => Reproduces(u.SelectMany(x => x).ToList()), budget: 16);
            f.MinimalSteps = units.SelectMany(u => u).Where(s => s.N > 0).Select(s => s.N).ToList();
            await store.SaveAsync(h, ct);
        }
        var shrunk = confirmed.Where(f => f.MinimalSteps is not null).ToList();
        if (shrunk.Count > 0)
            h.Notes.Add($"Shrinking: {shrunk.Count} confirmed repro{(shrunk.Count == 1 ? "" : "s")} cut from {shrunk.Sum(f => f.TrailSteps)} steps to {shrunk.Sum(f => f.MinimalSteps!.Count)} in total.");
    }

    // The hunt's setup hook: a POST to the target's own host (checked when the hunt was created), with an optional secret as bearer token.
    async Task<bool> SetupAsync(Hunt h, Dictionary<string, string> secrets, CancellationToken ct)
    {
        if (h.SetupUrl is null) return true;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, h.SetupUrl) { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
            if (h.SetupSecret is { } name && secrets.TryGetValue(name, out var value)) req.Headers.Authorization = new("Bearer", value);
            using var call = CancellationTokenSource.CreateLinkedTokenSource(ct); call.CancelAfter(TimeSpan.FromSeconds(20));
            using var res = await http.CreateClient().SendAsync(req, call.Token);
            return res.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested) { return false; }
    }

    // The exploration policy, and for Guided the model that plans destinations once the survey has drawn the map.
    async Task<(IHuntPolicy Policy, ModelHuntPolicy? Guide)> PolicyAsync(Hunt h, CancellationToken ct)
    {
        if (h.Strategy == "Heuristic") { h.Policy = "Heuristic"; return (new HeuristicHuntPolicy(), null); }
        var m = await gateway.ResolveAsync(string.IsNullOrEmpty(h.Connection) ? null : h.Connection, ct, "Medium");
        h.Model = new(m.Provider, m.Model, m.ExecutionMode);
        // The planner (strategy, rules, probe ideas) may be a stronger model than the driver that picks each step.
        var p = gateway.PlannerSelection(h.PlannerConnection) is { } sel && sel != (h.Connection ?? gateway.DefaultSelection) ? await gateway.ResolveAsync(sel, ct, "Medium") : null;
        if (p is not null) h.PlannerModel = new(p.Provider, p.Model, p.ExecutionMode);
        if (m.ExecutionMode != "Live")
        {
            h.Policy = "Heuristic";
            h.Notes.Add($"The selected model is {m.Provider} ({m.ExecutionMode}), not a live provider, so the deterministic heuristic explorer drove this hunt.");
            return (new HeuristicHuntPolicy(), null);
        }
        if (h.Strategy == "Guided")
        {
            h.Policy = "Guided";
            return (new HeuristicHuntPolicy(), new ModelHuntPolicy(gateway, m, p) { DraftsRules = false });
        }
        if (h.Strategy == "Hybrid")
        {
            h.Policy = "Hybrid";
            return (new HybridHuntPolicy(new HeuristicHuntPolicy(), new ModelHuntPolicy(gateway, m, p), Math.Max(5, h.MaxSteps / 2)), null);
        }
        h.Policy = "Model";
        return (new ModelHuntPolicy(gateway, m, p), null);
    }

    // Planner calls on a slow or local model can take minutes with no browser action in between; keep the session open.
    async Task KeepAliveAsync(string session, CancellationToken ct)
    {
        try { while (true) { await Task.Delay(TimeSpan.FromMinutes(2), ct); await worker.TouchAsync(session, ct); } }
        catch (OperationCanceledException) { /* the hunt finished */ }
    }

    // A project hunt checks what the twin predicts: its rules join the hunt's rules, its lifecycles and the project's
    // role permissions become predictions (Twin/Predictions.cs).
    async Task LoadPredictionsAsync(Hunt h)
    {
        if (await projects.GetAsync(h.ProjectId!, CancellationToken.None) is not { } p) return;
        var twin = p.TwinVersion > 0 ? await projects.TwinAsync(p.Id, p.TwinVersion, CancellationToken.None) : null;
        h.Predictions = Twin.PredictionSet.For(p, twin, h.Role);
        var added = 0;
        foreach (var r in twin?.Rules ?? [])
        {
            if (r.Status == "stale" || h.Invariants.Any(x => x.Check == r.Check && x.On == r.On)) continue; // stale: its story changed; rewritten before it is checked again
            var (rule, _) = InvariantRules.Create(null, r.On, r.Check, r.Statement, r.Story, "Twin", 0);
            if (rule is null) continue;
            if (rule.Requirement is { } key && h.Requirements.All(x => x.Key != key)) rule.Requirement = null;
            rule.Id = InvariantRules.Unique(h.Invariants, rule.Id);
            h.Invariants.Add(rule); added++;
        }
        h.Notes.Add($"Twin predictions: {h.Predictions.Lifecycles.Count} lifecycle transitions, {h.Predictions.Exclusive.Count} role-only areas, consistency of listed prices; {added} rules from the twin.");
    }

    static void AddPrediction(Hunt h, OracleState st, Twin.PredictionHit hit, HuntStep step, NetworkEvent e)
    {
        if (!st.Keys.Add(hit.Key)) return;
        var f = new Finding
        {
            Id = $"F{h.Findings.Count + 1}", Key = hit.Key, Cause = hit.Cause, Oracle = $"twin-{hit.Kind}", Provenance = "Prediction", Severity = "High",
            Title = hit.Title, Expected = hit.Expected, Actual = $"{hit.Actual} (prediction from {hit.Source})", Step = step.N, Url = step.Url,
            Evidence = e.Body is { Length: > 600 } b ? b[..600] + "…" : e.Body, RequestMethod = e.Method, RequestPath = e.Path,
        };
        h.Findings.Add(f); step.FindingIds.Add(f.Id);
        f.Repro = SpecWriter.Repro(h, f); f.Spec = SpecWriter.Write(h, f);
        if (h.Benchmark is { } bm)
            foreach (var d in bm.Seeded.Where(d => PredictionKinds.GetValueOrDefault(d.Id) == hit.Kind && Exhibits(d.Id, e, st, step)))
            {
                bm.PredictionCredits.TryAdd(d.Id, f.Id);
                bm.ExplainedPrediction.Add(f.Id);
            }
    }

    // A project hunt adds what it observed to the project's twin, as a new version.
    async Task UpdateTwinAsync(Hunt h, AppMap map, OracleState oracle)
    {
        try
        {
            if (await projects.GetAsync(h.ProjectId!, CancellationToken.None) is not { } p) return;
            var prev = p.TwinVersion > 0 ? await projects.TwinAsync(p.Id, p.TwinVersion, CancellationToken.None) : null;
            var twin = Twin.TwinBuilder.Next(prev, p, h, map, oracle.History);
            await projects.SaveTwinAsync(twin, CancellationToken.None);
            p.TwinVersion = twin.Version;
            await projects.SaveAsync(p, CancellationToken.None);
            h.TwinVersion = twin.Version;
            h.Notes.Add($"App twin v{twin.Version}: {twin.States.Count} states, {twin.Endpoints.Count} endpoints"
                + (twin.Changes.Empty ? "; nothing new." : $"; new: {twin.Changes.StatesAdded.Count} states, {twin.Changes.EndpointsAdded.Count} endpoints, {twin.Changes.FieldsAdded.Count} fields, {twin.Changes.StatusesAdded.Count} statuses."));
        }
        catch (IOException e) { h.Notes.Add($"The app twin was not updated: {e.Message}"); }
    }

    // Journeys: mine the outcomes this hunt reached, then prove the role's unproven journeys by replaying each twice in
    // fresh sessions, without a model, after the setup request. Secrets are this hunt's, so only its role is proven.
    async Task JourneysAsync(Hunt h, Dictionary<string, string> secrets, CancellationToken ct)
    {
        try
        {
            var twin = h.TwinVersion is { } tv ? await projects.TwinAsync(h.ProjectId!, tv, CancellationToken.None) : null;
            var library = await projects.JourneysAsync(h.ProjectId!, CancellationToken.None);
            foreach (var j in Journeys.JourneyMiner.Mine(h, twin))
            {
                var known = library.FirstOrDefault(x => x.Id == j.Id);
                if (known is null) { library.Add(j); await projects.SaveJourneyAsync(j, CancellationToken.None); }
                else if (known.Status != "proven" && j.Steps.Count < known.Steps.Count) { known.Steps = j.Steps; known.FromHunt = h.Id; known.Status = "candidate"; await projects.SaveJourneyAsync(known, CancellationToken.None); }
            }
            var replayer = new Replayer(worker);
            // Unproven journeys first (to prove them), then proven ones (the regression suite), at most six per hunt.
            foreach (var j in library.Where(j => j.Role == (h.Role ?? "default") && j.Source == "mined" && j.Review != "rejected").OrderBy(j => j.Status == "proven").Take(6))
            {
                var passes = 0; string? problem = null;
                for (var run = 0; run < 2; run++)
                {
                    try
                    {
                        if (h.SetupUrl is not null) await SetupAsync(h, secrets, ct);
                        var r = await replayer.RunAsync(h, j.Steps, secrets, null, ct);
                        var reached = r.Network.Any(e => Probes.Endpoint(e) == j.Outcome && e.Status is >= 200 and < 300);
                        if (r.Failed == 0 && reached) passes++;
                        else problem ??= r.Failed > 0 ? string.Join("; ", r.Problems.Take(2)) : $"{j.Outcome} did not succeed on replay";
                    }
                    catch (Exception e) when (e is BrowserWorkerException or HttpRequestException or OperationCanceledException) { problem ??= e.Message; }
                }
                j.Runs.Insert(0, new(DateTime.UtcNow, h.Id, passes == 2, problem));
                if (j.Runs.Count > 10) j.Runs.RemoveRange(10, j.Runs.Count - 10);
                var was = j.Status;
                j.Status = passes == 2 ? "proven" : passes == 1 ? "flaky" : "failing";
                if (passes == 2 && was != "proven") { j.ProvenAt = DateTime.UtcNow; j.Spec = SpecWriter.WriteJourney(h, j); }
                if (was == "proven" && passes < 2) h.Notes.Add($"Regression: the proven journey \"{j.Name}\" ({j.Role}) now {(passes == 1 ? "passes one replay of two" : "fails")}: {problem}");
                await projects.SaveJourneyAsync(j, CancellationToken.None);
                h.JourneysProven.Add($"{j.Name} ({j.Role}): {j.Status}, {passes}/2");
            }
            if (h.JourneysProven.Count > 0) h.Notes.Add($"Journeys: {string.Join("; ", h.JourneysProven)}.");
        }
        catch (IOException e) { h.Notes.Add($"Journeys were not updated: {e.Message}"); }
    }

    // The demo shop's catalog, for judging whether search results exhibit the seeded case defect (benchmark only).
    readonly List<(string Name, string Description)> catalog = [];

    // Scores the hunt against the defects seeded in the demo shop when the target is the demo shop.
    async Task<HuntBenchmark?> BenchmarkAsync(Hunt h, CancellationToken ct)
    {
        if (demo.AppFor(h.Url) is not { } app) return null;
        try
        {
            var defects = await demo.DefectsAsync(ct, app);
            if (app == DemoShopClient.Shop) catalog.AddRange(await demo.CatalogAsync(ct));
            return new HuntBenchmark { Target = DemoShopClient.Names[app], Seeded = defects.Where(d => d.Enabled).Select(d => new SeededDefect(d.Id, d.Title, null)).ToList() };
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            h.Notes.Add($"The {DemoShopClient.Names[app]} admin API did not answer ({e.Message}), so this hunt has no benchmark.");
            return null;
        }
    }

    static ElementInfo? Target(PageSnapshot page, int r)
    {
        var e = page.Elements.FirstOrDefault(x => x.Ref == r);
        if (e is null) return null;
        var t = e.Clone();
        var same = page.Elements.Where(x => x.Role == e.Role && x.Name == e.Name).ToList();
        t.Dupes = same.Count; t.Nth = same.IndexOf(e);
        return t;
    }

    async Task ObserveAsync(Hunt h, HuntStep step, PageSnapshot snap, OracleState oracle, CancellationToken ct)
    {
        var page = Uri.TryCreate(snap.Url, UriKind.Absolute, out var u) ? u.AbsolutePath + u.Fragment.Split('?')[0] : snap.Url;
        var c = h.Coverage;
        if (!c.Pages.Contains(page)) c.Pages.Add(page);
        if (snap.Error is { } err && !h.Notes.Any(x => x.StartsWith("Page snapshot failed"))) h.Notes.Add($"Page snapshot failed at step {step.N}: {err}");
        c.Actions = h.Steps.Count(s => s.N > 0 && s.Ok);
        foreach (var e in snap.Network.Where(e => e.ResourceType is "xhr" or "fetch"))
        {
            c.Requests++;
            if (e.Method != "GET") c.FormsSubmitted++;
            var ep = $"{e.Method} {Regex.Replace(e.Path, @"/(\d+|[A-Z]+-\d+|p-\d+)(?=/|$)", "/{id}")}";
            if (!c.Endpoints.Contains(ep)) c.Endpoints.Add(ep);
            if (step.Requests.Count < 6) step.Requests.Add($"{e.Method} {e.Path} → {(e.Status == 0 ? e.Failed : e.Status.ToString())}");
            if (oracle.History.Count < 600) oracle.History.Add((step, e));
            if (e.Status != 0 && (e.Status is >= 200 and < 300 || !oracle.Samples.ContainsKey(ep)) && Sample(e) is { } sample) oracle.Samples[ep] = sample;
            foreach (var r in h.Invariants.Where(r => r.Backfilled && InvariantRules.Applies(r, e)))
                CheckRule(h, oracle, r, step, e, Previous(oracle, r, e));
        }
        if (h.Predictions is { } ps)
            foreach (var e in snap.Network)
                foreach (var hit in Twin.Predictions.Check(ps, oracle.Predicted, e, step.Action.Args?.GetValueOrDefault("dropAuth") == "true"))
                    AddPrediction(h, oracle, hit, step, e);
        var repeats = new List<Oracles.Hit>();
        foreach (var hit in Oracles.Evaluate(oracle, snap, repeats)) AddFinding(h, null, hit, step, snap.Url);
        // The same bug found again by a different driver (a probe after the sweep, say) is independent confirmation.
        foreach (var hit in repeats)
            if (h.Findings.FirstOrDefault(f => f.Key == hit.Key) is { } seen && h.Steps.FirstOrDefault(s => s.N == seen.Step)?.Driver != step.Driver && seen.ConfirmedBy.Count < 5)
            {
                var by = $"{step.Driver ?? "Step"} step {step.N}";
                if (!seen.ConfirmedBy.Any(c => c.StartsWith(step.Driver ?? "Step"))) seen.ConfirmedBy.Add(by);
            }
        if (snap.Screenshot is { Length: > 0 } shot) { await store.SaveShotAsync(h.Id, step.N, shot, ct); step.HasShot = true; }
        await store.SaveAsync(h, ct);
    }

    // Rules added since the last check are evaluated over every API call recorded so far, in order.
    static void Backfill(Hunt h, OracleState st)
    {
        foreach (var r in h.Invariants.Where(r => !r.Backfilled))
        {
            r.Backfilled = true;
            var all = st.History.Select(x => x.Event).ToList();
            for (var i = 0; i < st.History.Count; i++)
            {
                var (step, e) = st.History[i];
                if (InvariantRules.Applies(r, e)) CheckRule(h, st, r, step, e, InvariantRules.Prior(all.GetRange(0, i), r, e));
            }
        }
    }

    static System.Text.Json.Nodes.JsonNode? Previous(OracleState st, Invariant r, NetworkEvent e) =>
        InvariantRules.Prior(st.History.Select(x => x.Event).TakeWhile(x => !ReferenceEquals(x, e)).ToList(), r, e);

    static void CheckRule(Hunt h, OracleState st, Invariant r, HuntStep step, NetworkEvent e, System.Text.Json.Nodes.JsonNode? prev)
    {
        var o = InvariantRules.EvaluateWith(r, e, prev);
        if (o.Holds is null) return; // the response lacks a field the rule needs
        r.Checks++;
        if (o.Holds == true) { r.Holds++; if (r.Status == "Untested") r.Status = "Holding"; return; }
        r.Violations++; r.Status = "Violated";
        if (r.FindingId is null)
        {
            var values = string.Join(", ", o.Values.Select(kv => $"{kv.Key} = {kv.Value}"));
            var f = new Finding
            {
                Id = $"F{h.Findings.Count + 1}", Key = $"invariant:{r.Id}", Oracle = "invariant", Provenance = "Invariant",
                Title = $"Rule broken: {r.Claim}", Severity = r.Requirement is null ? "Medium" : "High", Step = step.N, Url = step.Url,
                Expected = $"{r.Claim} ({r.Check})", Actual = $"{e.Method} {e.Path} → HTTP {e.Status}" + (values.Length > 0 ? $": {values}." : "."),
                Evidence = e.Body is { Length: > 600 } b ? b[..600] + "…" : e.Body, RequestMethod = e.Method, RequestPath = e.Path, Rule = r.Check, Requirement = r.Requirement
            };
            h.Findings.Add(f); step.FindingIds.Add(f.Id); r.FindingId = f.Id;
            f.Repro = SpecWriter.Repro(h, f); f.Spec = SpecWriter.Write(h, f);
        }
        // Benchmark only: credit a seeded defect when the rule is about it and the response that broke it exhibits it.
        if (h.Benchmark is { } bm)
            foreach (var d in bm.Seeded.Where(d => About(r, d.Id) && Exhibits(d.Id, e, st, step)))
            {
                bm.InvariantCredits.TryAdd(d.Id, r.FindingId!);
                bm.ExplainedInvariant.Add(r.FindingId!);
            }
    }

    // A compact example of an endpoint's JSON (first item of each list, short strings) so the model can name real fields.
    static string? Sample(NetworkEvent e)
    {
        if (e.Json() is not { } body) return null;
        var text = $"{e.Method} {e.Path} → {e.Status} {Compact(body, 0)}";
        if (e.Method != "GET" && e.RequestJson() is { } req) text += $"  request {Compact(req, 0)}";
        return text.Length > 600 ? text[..600] + "…" : text;
    }

    static string Compact(JsonNode? n, int depth) => n switch
    {
        JsonObject o => depth > 3 ? "{…}" : "{" + string.Join(", ", o.Take(14).Select(kv => $"{kv.Key}: {Compact(kv.Value, depth + 1)}")) + (o.Count > 14 ? ", …" : "") + "}",
        JsonArray a => a.Count == 0 ? "[]" : $"[{Compact(a[0], depth + 1)}{(a.Count > 1 ? $", … {a.Count} items" : "")}]",
        JsonValue v when v.TryGetValue<string>(out var t) => JsonSerializer.Serialize(t.Length > 24 ? t[..24] + "…" : t),
        null => "null",
        _ => n.ToJsonString()
    };

    // Whether a response shows a seeded shop defect, judged from the simulator's own rules (benchmark scoring only).
    internal static bool Exhibits(string defect, NetworkEvent e, OracleState st, HuntStep? step = null)
    {
        var body = e.Json() as JsonObject; var ok = e.Status is >= 200 and < 300;
        static decimal? D(JsonNode? x) => InvariantRules.Number(x);
        switch (defect)
        {
            case "coupon-stacking":
                return body is not null && (D(body["discount"]) is decimal disc && D(body["subtotal"]) is decimal sub && sub > 0 && disc > sub
                    || body["coupons"] is JsonArray cs && cs.Select(c => c?.ToString()).Distinct().Count() < cs.Count);
            case "tax-rounding":
                return body is not null && D(body["subtotal"]) is decimal s && D(body["discount"]) is decimal dc && D(body["tax"]) is decimal tax
                    && tax != Math.Round((s - dc) * 0.08m, 2, MidpointRounding.AwayFromZero);
            case "negative-qty":
                return ok && e.Method is "POST" or "PUT" && e.Path.Contains("/cart/items") && D(e.RequestJson()?["qty"]) is <= 0;
            case "expired-card":
                return ok && e.Method == "POST" && e.Path.EndsWith("/checkout") && e.RequestJson()?["expiry"]?.ToString() is { } exp
                    && Regex.Match(exp, @"^(\d{1,2})\s*/\s*(\d{2})$") is { Success: true } m
                    && new DateTime(2000 + int.Parse(m.Groups[2].Value), Math.Clamp(int.Parse(m.Groups[1].Value), 1, 12), 1).AddMonths(1) <= DateTime.UtcNow;
            case "search-case":
                if (!ok || e.Method != "GET" || !e.Path.EndsWith("/products") || body?["items"] is not JsonArray items) return false;
                var q = Regex.Match(e.Url, @"[?&]q=([^&]*)") is { Success: true } qm ? Uri.UnescapeDataString(qm.Groups[1].Value.Replace('+', ' ')).Trim() : "";
                return q.Length > 0 && !e.Url.Contains("category=")
                    && items.Count < st.Catalog.Count(p => p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || p.Description.Contains(q, StringComparison.OrdinalIgnoreCase));
            case "no-stock-check":
                // The defect is an order accepted beyond stock, so only a successful order response shows it, not a full cart.
                return ok && e.Method == "POST" && Regex.IsMatch(e.Path, "checkout|order", RegexOptions.IgnoreCase) && (body?["items"] ?? body?["lines"]) is JsonArray lines
                    && lines.OfType<JsonObject>().Any(l => l["productId"]?.ToString() is { } id && st.Stock.TryGetValue(id, out var left) && D(l["qty"]) > left);
            // Demo Hotel: only an accepted booking shows these; no built-in oracle knows hotel rules, so only rules catch them.
            case "past-checkin":
                return Booked(e, body) && DateOnly.TryParse(body!["checkIn"]?.ToString(), out var din) && din < DateOnly.FromDateTime(DateTime.UtcNow);
            case "checkout-before-checkin":
                return Booked(e, body) && D(body!["nights"]) <= 0;
            case "over-capacity":
                return Booked(e, body) && D(body!["guests"]) is decimal g && (g < 1 || D(body["maxGuests"]) is decimal max && g > max);
            case "wrong-total":
                return Booked(e, body) && D(body!["nights"]) is decimal n and >= 1 && D(body["rate"]) is decimal rate && D(body["total"]) is decimal total
                    && total - (D(body["breakfastTotal"]) ?? 0) != n * rate;
            case "quote-mismatch":
                return Booked(e, body) && Listed(st, "/rooms", body!["roomId"]?.ToString(), "rate") is decimal quoted && D(body["rate"]) is decimal charged && charged != quoted;
            case "breakfast-once":
                return Booked(e, body) && body!["breakfast"]?.GetValue<bool>() == true && D(body["nights"]) is decimal bn and >= 1 && D(body["guests"]) is decimal bg && bg >= 1
                    && D(body["breakfastTotal"]) is decimal bt && Listed(st, "/rooms", body["roomId"]?.ToString(), "breakfastRate") is decimal br && bt != bn * bg * br;
            case "cancel-after-checkin":
                return ok && e.Method == "POST" && e.Path.EndsWith("/cancel") && body?["checkedInAt"] is JsonValue;
            case "desk-open":
                // Front-desk data or actions answered without a front-desk sign-in: a probe that dropped the session, or no
                // successful front-desk sign-in recorded before this call.
                return ok && e.Path.Contains("/staff/") && !e.Path.EndsWith("/login") && !e.Path.EndsWith("/logout")
                    && (step?.Action.Args?.GetValueOrDefault("dropAuth") == "true" || !SignedInBefore(st, e, x => x.Path.EndsWith("/staff/login")));
            // Demo shop, added with the order desk.
            case "staff-open":
                // Staff data or actions answered for a shopper: the last sign-in before this call was not the staff account.
                return ok && e.Path.Contains("/staff/") && !SignedInBefore(st, e, x => x.Path.EndsWith("/login") && x.RequestJson()?["email"]?.ToString()?.Equals("staff@shop.demo", StringComparison.OrdinalIgnoreCase) == true, lastLoginOnly: true);
            case "cancel-shipped":
                return ok && e.Method == "POST" && e.Path.EndsWith("/cancel") && (body?["shippedAt"] is JsonValue || body?["deliveredAt"] is JsonValue);
            case "listing-price-stale":
                // A cart or order line priced differently from the product list shown before it.
                return ok && (e.Path.Contains("/cart") || e.Path.Contains("/checkout") || e.Path.Contains("/orders")) && (body?["items"] ?? body?["lines"]) is JsonArray cartLines
                    && cartLines.OfType<JsonObject>().Any(l => D(l["price"]) is decimal paid && Listed(st, "/products", l["productId"]?.ToString(), "price") is decimal listed && paid != listed);
            case "gift-wrap-per-item":
                return ok && body?["giftWrap"]?.GetValue<bool>() == true && D(body["giftWrapFee"]) is decimal fee && fee > 4.99m;
        }
        return false;
    }

    internal static bool About(Invariant r, string defect) =>
        DefiningFields.GetValueOrDefault(defect, []).Any(f => f.StartsWith('/')
            ? r.On.Contains(f, StringComparison.OrdinalIgnoreCase)
            : r.Check.Contains(f, StringComparison.OrdinalIgnoreCase));

    // The value a list response recorded before showed for an item, for example a room's rate in the room list.
    static decimal? Listed(OracleState st, string listPath, string? id, string field)
    {
        if (id is null) return null;
        foreach (var (_, x) in Enumerable.Reverse(st.History))
            if (x.Method == "GET" && x.Path.EndsWith(listPath) && x.Status is >= 200 and < 300 && x.Json()?["items"] is JsonArray items
                && items.OfType<JsonObject>().FirstOrDefault(i => i["id"]?.ToString() == id) is { } item)
                return InvariantRules.Number(item[field]);
        return null;
    }

    // Whether a successful sign-in matching the test was recorded before e (lastLoginOnly: the most recent sign-in matches).
    static bool SignedInBefore(OracleState st, NetworkEvent e, Func<NetworkEvent, bool> isLogin, bool lastLoginOnly = false)
    {
        var before = st.History.Select(x => x.Event).TakeWhile(x => !ReferenceEquals(x, e)).Where(x => x.Method == "POST" && x.Path.EndsWith("/login") && x.Status is >= 200 and < 300).ToList();
        return lastLoginOnly ? before.LastOrDefault() is { } last && isLogin(last) : before.Any(isLogin);
    }

    static bool Booked(NetworkEvent e, JsonObject? body) => e.Status is >= 200 and < 300 && e.Method == "POST" && e.Path.EndsWith("/bookings") && body is not null;

    // Routine oracles report through here too; passing the oracle state dedupes them like the snapshot oracles.
    static void AddFinding(Hunt h, OracleState? state, Oracles.Hit hit, HuntStep step, string url)
    {
        if (state is not null && !state.Keys.Add(hit.Key)) return;
        var f = new Finding
        {
            Id = $"F{h.Findings.Count + 1}", Key = hit.Key, Oracle = hit.Oracle, Title = hit.Title, Severity = hit.Severity, Provenance = "Observed",
            Step = step.N, Url = hit.Url ?? url, Expected = hit.Expected, Actual = hit.Actual, Evidence = hit.Evidence,
            RequestMethod = hit.Method, RequestPath = hit.Path, AssertText = hit.AssertText
        };
        h.Findings.Add(f); step.FindingIds.Add(f.Id);
        f.Repro = SpecWriter.Repro(h, f); f.Spec = SpecWriter.Write(h, f);
    }

    static void AddJudged(Hunt h, Suspicion s, string url)
    {
        // The model sometimes "reports" its own tool trouble (a routine argument, a ref that vanished). That is not an app bug.
        if (SelfReport().IsMatch($"{s.Title} {s.Actual}")) { h.Notes.Add($"Ignored a suspicion about the hunter's own tools: \"{s.Title}\"."); return; }
        var key = "judged:" + Regex.Replace(s.Title.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (h.Findings.Any(f => f.Key == key)) return;
        var step = h.Steps.LastOrDefault();
        var f = new Finding
        {
            Id = $"F{h.Findings.Count + 1}", Key = key, Oracle = "model-judgement", Title = s.Title, Severity = s.Severity, Provenance = "Judged",
            Step = step?.N ?? 0, Url = url, Expected = s.Expected, Actual = s.Actual
        };
        h.Findings.Add(f); step?.FindingIds.Add(f.Id);
        if (step?.Driver == "Probe") { f.Repro = SpecWriter.Repro(h, f); f.Spec = SpecWriter.Write(h, f); }
    }

    static void Finish(Hunt h)
    {
        foreach (var f in h.Findings) { f.Repro = SpecWriter.Repro(h, f); f.Spec = SpecWriter.Write(h, f); }
        var confirmed = h.Findings.Count(f => f.Reproduced == "Confirmed");
        h.Usage.CostPerConfirmedBug = confirmed > 0 ? Math.Round(h.Usage.CostUsd / confirmed, 4) : null;
        // A rule that never held once is more likely wrong than the app: keep the finding, but say so and lower it.
        // So is one broken on at least three of every four responses it checked: a real bug rarely shows on nearly every call.
        foreach (var r in h.Invariants.Where(r => r.FindingId is not null && (r is { Holds: 0, Violations: >= 2 } || r.Violations >= 4 && r.Violations >= 3 * r.Holds)))
            if (h.Findings.FirstOrDefault(f => f.Id == r.FindingId) is { } f && !f.Actual.Contains("may be wrong"))
            {
                f.Severity = "Low";
                f.Actual += r.Holds == 0
                    ? $" This rule never held in {r.Checks} checks, so the rule itself may be wrong; review it before filing."
                    : $" This rule was broken on {r.Violations} of {r.Checks} responses, so the rule itself may be wrong; review it before filing.";
            }
        if (h.Benchmark is not { } b) return;
        var observed = h.Findings.Where(f => f.Provenance == "Observed").ToList();
        b.Seeded = b.Seeded.Select(d => observed.FirstOrDefault(f => SeededKeys.GetValueOrDefault(d.Id, []).Contains(f.Key)) is { } hit
            ? d with { FindingId = hit.Id, Via = "oracle" }
            : b.InvariantCredits.TryGetValue(d.Id, out var rule) ? d with { FindingId = rule, Via = "rule" }
            : b.PredictionCredits.TryGetValue(d.Id, out var pred) ? d with { FindingId = pred, Via = "prediction" } : d with { FindingId = null, Via = null }).ToList();
        var explained = b.Seeded.SelectMany(d => SeededKeys.GetValueOrDefault(d.Id, []).Concat(Consequences.GetValueOrDefault(d.Id, []))).ToHashSet();
        b.ConfirmedDetected = b.Seeded.Count(d => d.FindingId is { } id && h.Findings.FirstOrDefault(f => f.Id == id)?.Reproduced == "Confirmed");
        b.ObservedFindings = observed.Count;
        b.UnmatchedObserved = observed.Count(f => !explained.Contains(f.Key));
        var rules = h.Findings.Where(f => f.Provenance == "Invariant").ToList();
        b.InvariantFindings = rules.Count;
        b.UnmatchedInvariant = rules.Count(f => !b.ExplainedInvariant.Contains(f.Id));
        // Of those, the ones the engine itself labelled as a probably wrong rule: still false alarms, but marked as such.
        b.UnmatchedInvariantFlagged = rules.Count(f => !b.ExplainedInvariant.Contains(f.Id) && f.Actual.Contains("may be wrong"));
        var predicted = h.Findings.Where(f => f.Provenance == "Prediction").ToList();
        b.PredictionFindings = predicted.Count;
        b.UnmatchedPrediction = predicted.Count(f => !b.ExplainedPrediction.Contains(f.Id));
    }

    [GeneratedRegex(@"place order|pay now|\bpay\b|purchase|buy now|confirm order|submit order|delete|close account|transfer|send money", RegexOptions.IgnoreCase)] private static partial Regex Irreversible();
    [GeneratedRegex(@"\b(routine|argument|args|action contract|element \[\d+\]|\bref\b|refs|snapshot|the hunter|my (action|click|tool)|plan item|test plan|json|field name|the model)\b", RegexOptions.IgnoreCase)] private static partial Regex SelfReport();
    [GeneratedRegex(@"sign ?out|log ?out", RegexOptions.IgnoreCase)] private static partial Regex SignOut();
    [GeneratedRegex(@"sign ?out|log ?out|session|logout", RegexOptions.IgnoreCase)] private static partial Regex MissionAllowsSignOut();

    // AI-chosen steps may not sign out unless the mission is about sessions: signing out ends the session that
    // probes and later steps rely on (seen repeatedly). The deterministic sweep never signs out either.
    static string? SignOutBlocked(Hunt h, string? driver, HuntAction a, ElementInfo? target) =>
        driver is "Model" or "Routine" && a.Type is "click" or "press" && target is not null && SignOut().IsMatch(target.Name) && !MissionAllowsSignOut().IsMatch(h.Mission)
            ? "Signing out is blocked for AI-chosen steps: it would end the session the rest of the hunt uses. Mention sign-out in the mission to test it." : null;
}

// Runs hunts in the API process: secrets are handed over in memory and never persisted, so a hunt cannot resume on another node.
public class HuntRunner(IServiceScopeFactory scopes, HunterOptions opts, ILogger<HuntRunner> log) : BackgroundService
{
    // After: a hunt this one waits for (a survey runs its roles one after another, so their test-data resets do not collide).
    public record Job(string HuntId, Dictionary<string, string> Secrets, bool ConfirmOnly = false, string? After = null);

    readonly Channel<Job> _queue = Channel.CreateUnbounded<Job>();
    readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new();
    readonly ConcurrentDictionary<string, bool> _cancelled = new();
    readonly SemaphoreSlim _gate = new(Math.Max(1, opts.MaxConcurrentHunts));
    readonly ConcurrentDictionary<string, TaskCompletionSource> _finished = new();

    // Hunt id -> browser session while the hunt runs (for the live view).
    public ConcurrentDictionary<string, string> Live { get; } = new();
    // Short-lived viewing tickets: <img> and <video> cannot send a bearer token, so they present a ticket instead.
    readonly ConcurrentDictionary<string, (string Tenant, string Hunt, DateTime Expires)> _tickets = new();

    public string Ticket(string tenant, string hunt)
    {
        foreach (var t in _tickets.Where(t => t.Value.Expires < DateTime.UtcNow)) _tickets.TryRemove(t.Key, out _);
        var id = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        _tickets[id] = (tenant, hunt, DateTime.UtcNow.AddMinutes(30));
        return id;
    }

    public (string Tenant, string Hunt)? Redeem(string ticket) =>
        _tickets.TryGetValue(ticket, out var t) && t.Expires > DateTime.UtcNow ? (t.Tenant, t.Hunt) : null;

    public static string VideoPath(HunterOptions opts, string hunt) =>
        Path.Combine(opts.MediaDir, System.Text.RegularExpressions.Regex.Replace(hunt, @"[^\w-]", "_") + ".webm");

    public void Enqueue(Job j)
    {
        _finished.TryAdd(j.HuntId, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        _queue.Writer.TryWrite(j);
    }
    public bool IsActive(string id) => _active.ContainsKey(id);
    public void Cancel(string id) { _cancelled[id] = true; if (_active.TryGetValue(id, out var c)) c.Cancel(); }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await MarkInterruptedAsync();
        await foreach (var job in _queue.Reader.ReadAllAsync(stop))
            _ = Task.Run(async () =>
            {
                try
                {
                    // Wait for the predecessor before taking a slot, so a waiting hunt never blocks one that could run.
                    if (job.After is { } after && _finished.TryGetValue(after, out var prev)) await prev.Task.WaitAsync(stop);
                    await _gate.WaitAsync(stop);
                    try { await RunAsync(job, stop); } finally { _gate.Release(); }
                }
                catch (OperationCanceledException) { /* shutting down */ }
                finally { if (_finished.TryRemove(job.HuntId, out var done)) done.TrySetResult(); }
            }, CancellationToken.None);
    }

    async Task RunAsync(Job job, CancellationToken stop)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        _active[job.HuntId] = cts;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            var store = sp.GetRequiredService<HuntStore>();
            if (await store.GetAsync(job.HuntId, CancellationToken.None) is not { } h) return;
            if (_cancelled.ContainsKey(job.HuntId)) { h.Status = "Cancelled"; h.FinishedAt = DateTime.UtcNow; await store.SaveAsync(h, CancellationToken.None); return; }
            if (job.ConfirmOnly) await sp.GetRequiredService<HuntEngine>().ReconfirmAsync(h, job.Secrets, cts.Token);
            else await sp.GetRequiredService<HuntEngine>().RunAsync(h, job.Secrets, () => _cancelled.ContainsKey(job.HuntId), cts.Token);
        }
        catch (Exception e) { log.LogError(e, "Hunt {HuntId} crashed", job.HuntId); }
        finally { _active.TryRemove(job.HuntId, out _); _cancelled.TryRemove(job.HuntId, out _); }
    }

    // Hunts that were queued or running when the process stopped cannot continue (their secrets were only in memory).
    async Task MarkInterruptedAsync()
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<HuntStore>();
            foreach (var h in (await store.ListAsync(CancellationToken.None)).Where(h => h.Status is "Queued" or "Running" or "Confirming"))
            {
                h.Status = "Interrupted"; h.Error = "The API restarted while this hunt was in progress.";
                await store.SaveAsync(h, CancellationToken.None);
            }
        }
        catch (Exception e) { log.LogWarning(e, "Could not mark interrupted hunts"); }
    }
}
