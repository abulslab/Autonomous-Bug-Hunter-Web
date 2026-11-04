using BugHunter.Api.Hunter;
using BugHunter.Api.Journeys;
using BugHunter.Api.Learning;
using BugHunter.Api.Projects;
using BugHunter.Api.Runs;
using BugHunter.Api.Twin;

namespace BugHunter.Tests;

public class LearningTests
{
    [Fact]
    public void A_story_change_reaches_its_features_rules_journeys_and_endpoints()
    {
        var k = new KnowledgeSnapshot { Version = 3, Diff = new() { StoriesChanged = [new("HOTEL-3", 1, 2, null, null, ["+ breakfast is per night"], [])], StoriesAdded = ["HOTEL-7"] } };
        var twin = new AppTwin
        {
            Features = [new TwinFeature { Name = "Book a stay", Stories = ["HOTEL-3"], Endpoints = ["POST /api/bookings", "GET /api/rooms"] },
                        new TwinFeature { Name = "Front desk", Stories = ["HOTEL-5"], Endpoints = ["GET /api/staff/bookings"] }],
            Rules = [new TwinRule { On = "POST /api/bookings", Check = "response.total > 0", Story = "HOTEL-3", Status = "accepted" },
                     new TwinRule { On = "GET /api/staff/bookings", Check = "status == 200", Story = "HOTEL-5", Status = "accepted" }],
        };
        var journeys = new List<Journey>
        {
            new() { Id = "guest-book", Outcome = "POST /api/bookings", Status = "proven", Review = "accepted" },
            new() { Id = "desk-in", Outcome = "POST /api/staff/bookings/{id}/checkin", Stories = ["HOTEL-5"], Status = "proven", Review = "accepted" },
        };

        var i = ChangeAnalysis.ForStories(k, twin, journeys);
        Assert.Equal(["HOTEL-3", "HOTEL-7"], i.Stories);
        Assert.Equal(["Book a stay"], i.Features);
        Assert.Equal(["GET /api/rooms", "POST /api/bookings"], i.Endpoints);
        Assert.Equal(["POST /api/bookings response.total > 0"], i.Rules);
        Assert.Equal(["guest-book"], i.Journeys);        // by its outcome endpoint

        ChangeAnalysis.Apply(i, twin, journeys);
        Assert.Equal("stale", twin.Rules[0].Status);      // rewritten from the new criteria before it is checked again
        Assert.Equal("accepted", twin.Rules[1].Status);
        Assert.Equal("candidate", journeys[0].Status);    // proven again, and reviewed again
        Assert.Null(journeys[0].Review);
        Assert.Equal("proven", journeys[1].Status);

        var focus = ChangeAnalysis.Focus(i, ["POST /api/coupons"]);
        Assert.Contains("HOTEL-3, HOTEL-7", focus);
        Assert.Contains("areas where real bugs were found before: POST /api/coupons", focus);
    }

    [Fact]
    public void Triage_sets_priorities_and_suppresses_what_is_not_a_bug()
    {
        TriageRecord T(string key, string outcome, string ep) => new(key, key, outcome, null, "person:qa", DateTime.UtcNow, ep);
        var triage = new[] { T("a", "bug", "POST /api/cart"), T("b", "bug", "POST /api/cart"), T("c", "known-issue", "GET /api/rooms"), T("d", "not-a-bug", "GET /api/x") };
        Assert.Equal(["POST /api/cart", "GET /api/rooms"], ChangeAnalysis.Priorities(triage));
        Assert.True(triage[3].Suppresses); Assert.False(triage[2].Suppresses);

        var h = new Hunt { Id = "H-1", Findings = [new Finding { Id = "F1", Key = "a", Title = "A" }, new Finding { Id = "F2", Key = "d", Title = "D" }] };
        var s = RunCompare.Snapshot([h], null, [], null, new HashSet<string> { "d" });
        Assert.Equal(["a"], s.Findings.Keys);

        var twin = new AppTwin { Rules = [new TwinRule { On = "POST /api/cart", Check = "response.qty > 0", Statement = "qty", Status = "accepted" }] };
        var hunt = new Hunt { Invariants = [new Invariant { On = "POST /api/cart", Check = "response.qty > 0", Checks = 3, Holds = 3 }] };
        Assert.Equal(new LibraryRule("POST /api/cart", "response.qty > 0", "qty", null, 1, 3, 0), ChangeAnalysis.Library(twin, [hunt, new Hunt()]).Single());
    }
}
