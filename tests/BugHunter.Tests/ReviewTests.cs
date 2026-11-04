using BugHunter.Api.Journeys;
using BugHunter.Api.Projects;
using BugHunter.Api.Review;
using BugHunter.Api.Runs;
using BugHunter.Api.Twin;

namespace BugHunter.Tests;

public class ReviewTests
{
    static ReviewItem Decided(string id, bool judge, bool person) => new()
    {
        Id = id, Kind = "rule", Judge = new("judge:ollama:llama3.2", judge, "", DateTime.UtcNow), Decision = new("person:qa", person, "", DateTime.UtcNow),
    };

    [Fact]
    public void The_judge_decides_only_when_allowed_from_another_family_and_agreeing_with_people()
    {
        Assert.Equal("qwen", ReviewGate.Family("Ollama:qwen2.5:7b"));
        Assert.Equal("llama", ReviewGate.Family("ollama:llama3.2"));
        Assert.Equal("claude", ReviewGate.Family("anthropic:claude-sonnet-4-5"));

        var rule = new ReviewItem { Id = "rule-1", Kind = "rule" };
        var s = new ReviewSettings { JudgeModel = "ollama:llama3.2", Bypass = true, MinAgreementSample = 4, AgreementThreshold = 0.75 };
        var good = ReviewGate.Agreement([Decided("a", true, true), Decided("b", false, false), Decided("c", true, true), Decided("d", true, false)]);
        Assert.Equal(4, good.Compared); Assert.Equal(0.75, good.Rate);

        Assert.True(ReviewGate.CanApply(rule, s, good, "Ollama:qwen2.5:7b").Apply);
        Assert.False(ReviewGate.CanApply(rule, s, good, "ollama:llama3.1:8b").Apply);                                 // same family as the planner
        Assert.False(ReviewGate.CanApply(rule, new() { JudgeModel = s.JudgeModel, Bypass = false }, good, "Ollama:qwen2.5:7b").Apply); // bypass off
        Assert.False(ReviewGate.CanApply(rule, s, new Agreement(3, 3), "Ollama:qwen2.5:7b").Apply);                     // too few to compare
        Assert.False(ReviewGate.CanApply(rule, s, new Agreement(10, 6), "Ollama:qwen2.5:7b").Apply);                    // agreement too low
        Assert.False(ReviewGate.CanApply(new() { Kind = "behaviour" }, s, good, "Ollama:qwen2.5:7b").Apply);           // only people accept behaviour
        Assert.False(ReviewGate.CanApply(new() { Kind = "baseline", Blocking = true }, s, good, "Ollama:qwen2.5:7b").Apply); // contradicts intent

        // Judge-applied decisions do not count as agreement with people.
        Assert.Equal(0, ReviewGate.Agreement([new() { Judge = new("judge:x", true, "", DateTime.UtcNow), Decision = new("judge:x", true, "", DateTime.UtcNow) }]).Compared);
        // Spot checks are stable and roughly at the rate.
        Assert.Equal(ReviewGate.Sampled("rule-abc", 0.2), ReviewGate.Sampled("rule-abc", 0.2));
        var share = Enumerable.Range(0, 1000).Count(i => ReviewGate.Sampled($"item-{i}", 0.2)) / 1000.0;
        Assert.InRange(share, 0.12, 0.28);
    }

    [Fact]
    public void The_queue_holds_unreviewed_rules_proven_journeys_unexplained_changes_and_a_baseline_update()
    {
        var p = new Project { Id = "hotel", BaselineRun = "R-1" };
        var twin = new AppTwin
        {
            Rules = [new TwinRule { On = "POST /api/bookings", Check = "response.total > 0", Statement = "a stay costs something", Status = "inferred" },
                     new TwinRule { On = "POST /api/bookings", Check = "response.nights > 0", Statement = "accepted already", Status = "accepted" }],
        };
        var journeys = new List<Journey>
        {
            new() { Id = "guest-book", Name = "Book a stay", Role = "guest", Status = "proven", Outcome = "POST /api/bookings" },
            new() { Id = "guest-cancel", Name = "Cancel", Role = "guest", Status = "candidate" },
            new() { Id = "desk-in", Name = "Check in", Role = "front-desk", Status = "proven", Review = "accepted" },
        };
        var c = new RunComparison
        {
            FindingsNew = [new("Front desk open", "Prediction", "Confirmed", "H-2", "F1")],
            BehaviourChanged = [new("GET /api/rooms", "guest 200 | rate", "guest 200 | rate,x", []), new("POST /api/bookings", "a", "b", ["HOTEL-3"])],
        };
        Assert.False(c.OnlyImprovements); Assert.True(c.Blocking);
        var items = ReviewQueue.Collect(p, twin, journeys, new ProjectRun { Id = "R-2", Snapshot = new() }, c, []);
        Assert.Equal(["rule", "journey", "behaviour", "baseline"], items.Select(i => i.Kind));
        Assert.Equal("GET /api/rooms", items.Single(i => i.Kind == "behaviour").Subject); // the change a story explains is not asked
        Assert.True(items.Single(i => i.Kind == "baseline").Blocking);
        Assert.Empty(ReviewQueue.Collect(p, twin, journeys, new ProjectRun { Id = "R-2", Snapshot = new() }, c, items)); // stable ids: asked once

        // A change a reviewer accepted is no longer a signal, and improvements alone advance the baseline.
        var accepted = new RunComparison { FindingsFixed = [new("x", "Prediction", null, "H", "F")], BehaviourChanged = [new("GET /api/rooms", "a", "b", [], Accepted: true)] };
        Assert.True(accepted.OnlyImprovements);
    }
}
