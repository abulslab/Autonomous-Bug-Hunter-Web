using BugHunter.Api.Hunter;
using BugHunter.Api.Journeys;
using BugHunter.Api.Learning;
using BugHunter.Api.Projects;
using BugHunter.Api.Review;
using BugHunter.Api.Runs;
using BugHunter.Api.Workflow;

namespace BugHunter.Tests;

public class ReadinessTests
{
    static readonly KnowledgeSnapshot K = new() { Version = 2, Stories = [new("HOTEL-3", "Price", "total", 2, "h", null, null)] };
    static ProjectRun Run(params (string Key, string Reproduced)[] findings)
    {
        var s = new RunSnapshot();
        foreach (var (key, rep) in findings) s.Findings[key] = new(key, "Prediction", rep, "H-1", "F1");
        return new() { Id = "R-2", Snapshot = s };
    }
    static readonly List<Journey> Proven = [new() { Id = "j", Name = "Book", Role = "guest", Status = "proven", Stories = ["HOTEL-3"] }];

    [Fact]
    public void The_verdict_comes_from_what_is_proven()
    {
        var p = new Project { Id = "hotel", Name = "Hotel" };
        var hunts = new Dictionary<string, Hunt>();

        Assert.Equal("not-ready", Readiness.Build(p, K, null, Proven, null, null, hunts, []).Verdict);                      // nothing tested
        Assert.Equal("ready", Readiness.Build(p, K, null, Proven, Run(), new RunComparison(), hunts, []).Verdict);

        var bug = Readiness.Build(p, K, null, Proven, Run(("desk-open", "Confirmed")), null, hunts, []);
        Assert.Equal("not-ready", bug.Verdict);
        Assert.Contains("1 confirmed bug open", bug.Blockers[0]);

        p.Triage["desk-open"] = new("desk-open", "desk-open", "known-issue", null, "person:qa", DateTime.UtcNow, null, "HOTEL-12");
        var known = Readiness.Build(p, K, null, Proven, Run(("desk-open", "Confirmed")), null, hunts, []);
        Assert.Equal("at-risk", known.Verdict);                                                    // shipping a known issue is a risk
        Assert.Equal("HOTEL-12", known.OpenBugs.Single().Issue);

        var regressed = Readiness.Build(new Project(), K, null, Proven, Run(), new RunComparison { JourneysRegressed = ["guest-book: failing"] }, hunts, []);
        Assert.Equal("not-ready", regressed.Verdict);

        var pending = Readiness.Build(new Project { PendingChange = new ChangeImpact { Stories = ["HOTEL-3"] } }, K, null, Proven, Run(), null, hunts,
            [new ReviewItem { Status = "pending", Kind = "rule" }]);
        Assert.Equal("at-risk", pending.Verdict);
        Assert.Equal(2, pending.Risks.Count);

        var md = Readiness.Markdown(bug);
        Assert.Contains("# Release readiness: Hotel", md);
        Assert.Contains("**Not ready**", md);
        Assert.Contains("| HOTEL-3 Price | v2 | yes |", md);
    }

    [Fact]
    public void Markdown_becomes_confluence_storage()
    {
        var s = AtlassianWriter.Storage("# Title\n**Ready**. a & b\n\n- one\n- two\n\n| A | B |\n|---|---|\n| 1 | 2 |\n");
        Assert.Equal("<h1>Title</h1><p><strong>Ready</strong>. a &amp; b</p><ul><li>one</li><li>two</li></ul><table><tbody><tr><th>A</th><th>B</th></tr><tr><td>1</td><td>2</td></tr></tbody></table>", s);
    }
}
