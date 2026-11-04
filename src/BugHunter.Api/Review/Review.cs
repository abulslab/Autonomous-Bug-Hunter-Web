using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BugHunter.Api.Common;
using BugHunter.Api.Contracts;
using BugHunter.Api.Models;
using BugHunter.Api.Projects;
using BugHunter.Api.Twin;

namespace BugHunter.Api.Review;

// The review gate: what a model proposed, or what the app started doing, becomes part of what Bug Hunter expects only
// after a decision. People decide; an LLM judge may decide in their place only when the project allows it, the judge is
// from another model family than the one that proposed, and its measured agreement with people is high enough. Every
// decision is logged with its reason and can be reversed.
public class ReviewItem
{
    public string Id { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string Kind { get; set; } = "";          // rule | journey | behaviour | baseline
    public string Subject { get; set; } = "";       // what it is about: "POST /api/bookings: total == nights * rate", a journey id, an endpoint, a run id
    public string Summary { get; set; } = "";
    public string? Evidence { get; set; }
    public string Accepting { get; set; } = "";     // what accepting means, in plain words
    public string Status { get; set; } = "pending"; // pending | accepted | rejected
    public Dictionary<string, string> Data { get; set; } = new(); // what applying the decision needs (rule on/check, endpoint, run)
    public bool Blocking { get; set; }              // it contradicts what is expected (new bugs, regressions): never decided by the judge
    public ReviewDecision? Decision { get; set; }
    public ReviewDecision? Judge { get; set; }      // the judge's verdict, kept even when a person decides
    public bool SpotCheck { get; set; }             // a judge decision a person should look at
    public List<ReviewDecision> History { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public record ReviewDecision(string By, bool Accept, string Reason, DateTime At);

public class ReviewSettings
{
    public string? JudgeModel { get; set; }              // e.g. ollama:llama3, from another family than the planner
    public bool Bypass { get; set; }                     // let the judge decide when the conditions below hold
    public double AgreementThreshold { get; set; } = 0.9;
    public int MinAgreementSample { get; set; } = 5;     // decisions both a person and the judge made, before bypass is trusted
    public double SpotCheckRate { get; set; } = 0.2;     // share of judge decisions routed to a person
}

public record Agreement(int Compared, int Agreed)
{
    public double Rate => Compared == 0 ? 0 : (double)Agreed / Compared;
}

public static partial class ReviewGate
{
    // A model family: qwen, llama, gemini, claude, gpt... The judge must not grade its own family's work.
    public static string Family(string? model)
    {
        var m = (model ?? "").ToLowerInvariant();
        var name = m.Contains(':') ? m[(m.IndexOf(':') + 1)..] : m;
        return FamilyName().Match(name) is { Success: true } x ? x.Value : name;
    }

    public static Agreement Agreement(IEnumerable<ReviewItem> items) =>
        items.Where(i => i.Judge is not null && i.Decision is { } d && !d.By.StartsWith("judge")).Aggregate(new Agreement(0, 0),
            (a, i) => new(a.Compared + 1, a.Agreed + (i.Judge!.Accept == i.Decision!.Accept ? 1 : 0)));

    // Whether the judge's verdict may be applied without a person. Behaviour changes never: turning what the app does
    // into what it should do is a person's decision (principle 2).
    public static (bool Apply, string Why) CanApply(ReviewItem item, ReviewSettings s, Agreement agreement, string? plannerModel)
    {
        if (item.Kind == "behaviour") return (false, "accepting observed behaviour as intended always needs a person");
        if (item.Blocking) return (false, "it contradicts what is expected (new findings or regressions)");
        if (!s.Bypass) return (false, "the project does not let the judge decide");
        if (Family(s.JudgeModel) == Family(plannerModel)) return (false, $"the judge ({s.JudgeModel}) is from the same family as the planner ({plannerModel})");
        if (agreement.Compared < s.MinAgreementSample) return (false, $"only {agreement.Compared} decisions to compare with people (needs {s.MinAgreementSample})");
        if (agreement.Rate < s.AgreementThreshold) return (false, $"agreement with people is {agreement.Rate:P0}, below {s.AgreementThreshold:P0}");
        return (true, $"agreement with people {agreement.Rate:P0} over {agreement.Compared} decisions");
    }

    // Spot checks are chosen deterministically from the item id, so the same item is always (or never) sampled.
    public static bool Sampled(string id, double rate) => rate > 0 && Convert.ToUInt32(KnowledgeDiffer.Hash(id)[..6], 16) % 1000 < rate * 1000;

    public static readonly JsonObject Schema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["verdicts"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["id"] = new JsonObject { ["type"] = "string" }, ["accept"] = new JsonObject { ["type"] = "boolean" }, ["reason"] = new JsonObject { ["type"] = "string" } } }
            }
        }
    };

    public const string System = """
You review proposals for an automated QA system against the product's user stories and documents. For each item say
whether to accept it, with a one-sentence reason grounded in the stories or documents:
- a rule: accept if the stories or documents say it must always hold, reject if they do not or it is stated wrongly;
- a journey: accept if it is something a real user of that role should be able to do;
- a baseline: accept if nothing in the run contradicts the stories.
Reply with one JSON object: {"verdicts":[{"id":"...","accept":true,"reason":"..."}]}.
""";

    public static string Prompt(KnowledgeSnapshot? k, IReadOnlyList<ReviewItem> items)
    {
        var sb = new StringBuilder();
        if (k is not null)
        {
            sb.AppendLine("STORIES:");
            foreach (var s in k.Stories) sb.AppendLine($"- {s.Key} {s.Title}: {Regex.Replace(s.Criteria, @"\s*\n\s*", " / ")}");
            sb.AppendLine("DOCUMENTS:\n" + KnowledgeDiffer.Grounding(k, 2500));
        }
        sb.AppendLine("ITEMS:");
        foreach (var i in items) sb.AppendLine($"- id {i.Id} [{i.Kind}] {i.Summary}{(i.Evidence is { Length: > 0 } e ? $" (evidence: {e})" : "")}");
        return sb.ToString();
    }

    public static async Task<List<(string Id, bool Accept, string Reason)>> JudgeAsync(ModelGateway gateway, ResolvedModel m, KnowledgeSnapshot? k, IReadOnlyList<ReviewItem> items, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(m.Provider is "Ollama" or "Local" ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(2));
        var res = await gateway.CompleteAsync(m, new ChatRequest(System, Prompt(k, items), 0, 1500, [], "bug-hunter-judge", null, Schema), deadline.Token);
        var parsed = OutputContracts.Parse(res.Text).Value as JsonObject;
        return (parsed?["verdicts"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(v => (J.Text(v["id"]), v["accept"]?.GetValue<bool>() == true, J.Text(v["reason"])))
            .Where(v => items.Any(i => i.Id == v.Item1)).ToList();
    }

    [GeneratedRegex(@"^[a-z]+")] private static partial Regex FamilyName();
}

// What waits for review, collected from the project's current state. Ids are stable, so an item decided once is not
// asked again unless what it is about changes (a rule rewritten, a different behaviour, another run).
public static class ReviewQueue
{
    static string H(string s) => KnowledgeDiffer.Hash(s)[..10];

    public static List<ReviewItem> Collect(Project p, AppTwin? twin, IEnumerable<Journeys.Journey> journeys, Runs.ProjectRun? latest, Runs.RunComparison? comparison, IEnumerable<ReviewItem> existing)
    {
        var known = existing.Select(i => i.Id).ToHashSet();
        var add = new List<ReviewItem>();
        void Add(ReviewItem i) { i.ProjectId = p.Id; if (known.Add(i.Id)) add.Add(i); }

        foreach (var r in twin?.Rules.Where(r => r.Status is "inferred" or "verified") ?? [])
            Add(new()
            {
                Id = $"rule-{H(r.On + " " + r.Check)}", Kind = "rule", Subject = $"{r.On}: {r.Check}",
                Summary = $"Rule{(r.Story is null ? "" : $" from {r.Story}")}: {r.Statement} (on {r.On}, check {r.Check})",
                Evidence = r.Evidence.FirstOrDefault()?.Note, Accepting = "the rule becomes an expectation: a violation is a finding and fails the gate",
                Data = { ["on"] = r.On, ["check"] = r.Check, ["statement"] = r.Statement, ["story"] = r.Story ?? "" },
            });
        foreach (var j in journeys.Where(j => j.Source == "mined" && j.Status == "proven" && j.Review is null))
            Add(new()
            {
                Id = $"journey-{j.Id}", Kind = "journey", Subject = j.Id,
                Summary = $"Journey \"{j.Name}\" as {j.Role}: {j.Steps.Count} steps ending in {j.Outcome}{(j.Stories.Count > 0 ? $" (stories {string.Join(", ", j.Stories)})" : "")}",
                Evidence = $"proven by replay {j.Runs.Count(r => r.Passed)} times", Accepting = "the journey joins the regression gate: if it stops working, the gate fails",
                Data = { ["journey"] = j.Id },
            });
        if (latest?.Snapshot is not null && comparison is not null && latest.Id != p.BaselineRun)
        {
            foreach (var b in comparison.BehaviourChanged.Where(b => b.NeedsReview))
                Add(new()
                {
                    Id = $"behaviour-{H(b.Endpoint + "|" + b.After)}", Kind = "behaviour", Subject = b.Endpoint,
                    Summary = $"{b.Endpoint} changed with no story change: {b.Before} -> {b.After}", Evidence = $"run {latest.Id}",
                    Accepting = "the new behaviour is intended; it is no longer reported as a change",
                    Data = { ["endpoint"] = b.Endpoint, ["after"] = b.After },
                });
            if (!comparison.OnlyImprovements)
                Add(new()
                {
                    Id = $"baseline-{latest.Id}", Kind = "baseline", Subject = latest.Id, Blocking = comparison.Blocking,
                    Summary = $"Make run {latest.Id} the baseline: {comparison.FindingsNew.Count} new findings, {comparison.FindingsFixed.Count} fixed, {comparison.JourneysRegressed.Count} journeys regressed, "
                        + $"{comparison.BehaviourChanged.Count(b => b.NeedsReview)} unexplained behaviour changes, {comparison.EndpointsAdded.Count} endpoints added, {comparison.EndpointsRemoved.Count} removed",
                    Evidence = comparison.FindingsNew.Count > 0 ? "new: " + string.Join("; ", comparison.FindingsNew.Take(3).Select(f => f.Title)) : null,
                    Accepting = "later runs are compared with this one",
                    Data = { ["run"] = latest.Id, ["previous"] = p.BaselineRun ?? "" },
                });
        }
        return add;
    }
}

// Applies a decision to what it is about. Every decision is reversible: deciding again the other way undoes it.
public class ReviewService(ProjectStore store)
{
    public async Task DecideAsync(Project p, ReviewItem item, bool accept, string by, string reason, CancellationToken ct)
    {
        if (item.Decision is { } prev) item.History.Insert(0, prev);
        item.Decision = new(by, accept, reason, DateTime.UtcNow);
        item.Status = accept ? "accepted" : "rejected";
        if (!by.StartsWith("judge")) item.SpotCheck = false; // a person looked at it
        switch (item.Kind)
        {
            case "rule":
            {
                var twin = p.TwinVersion > 0 ? await store.TwinAsync(p.Id, p.TwinVersion, ct) : null;
                if (twin is null) break;
                var on = item.Data["on"]; var check = item.Data["check"]; var key = $"{on} {check}";
                var rule = twin.Rules.FirstOrDefault(r => r.On == on && r.Check == check);
                if (accept)
                {
                    p.RetiredRules.Remove(key);
                    twin.Rejected.RemoveAll(r => r.Kind == "rule" && r.Name == item.Data["statement"]);
                    if (rule is null) twin.Rules.Add(rule = new() { On = on, Check = check, Statement = item.Data["statement"], Story = item.Data["story"] is { Length: > 0 } s ? s : null });
                    rule.Status = "accepted";
                }
                else
                {
                    if (!p.RetiredRules.Contains(key)) p.RetiredRules.Add(key);
                    if (rule is not null) twin.Rules.Remove(rule);
                    twin.Rejected.Add(new("rule", item.Data["statement"], $"rejected by {by}: {reason}"));
                }
                await store.SaveTwinAsync(twin, ct);
                break;
            }
            case "journey":
                if ((await store.JourneysAsync(p.Id, ct)).FirstOrDefault(j => j.Id == item.Data["journey"]) is { } jo) { jo.Review = accept ? "accepted" : "rejected"; await store.SaveJourneyAsync(jo, ct); }
                break;
            case "behaviour":
                if (accept) p.AcceptedBehaviour[item.Data["endpoint"]] = item.Data["after"]; else p.AcceptedBehaviour.Remove(item.Data["endpoint"]);
                break;
            case "baseline":
            {
                var runId = item.Data["run"];
                if (accept && await store.RunAsync(p.Id, runId, ct) is { } r)
                {
                    r.Accepted = true; r.AcceptedAt = DateTime.UtcNow; r.AcceptedBy = by; await store.SaveRunAsync(r, ct);
                    p.BaselineRun = runId;
                }
                else if (!accept && p.BaselineRun == runId && item.Data["previous"] is { Length: > 0 } before) p.BaselineRun = before; // reversed
                break;
            }
        }
        await store.SaveAsync(p, ct);
        await store.SaveReviewAsync(item, ct);
    }
}
