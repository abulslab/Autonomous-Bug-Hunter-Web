using BugHunter.Api.Hunter;
using BugHunter.Api.Journeys;
using BugHunter.Api.Projects;
using BugHunter.Api.Twin;

namespace BugHunter.Tests;

public class JourneyTests
{
    static HuntStep Step(int n, string type, params string[] requests) =>
        new() { N = n, Ok = true, Action = new HuntAction { Type = type }, Requests = [.. requests] };

    static Hunt Hunt() => new()
    {
        Id = "H-1", ProjectId = "hotel", Role = "guest",
        Steps =
        [
            Step(0, "open", "GET /api/rooms → 200"),
            Step(1, "click", "POST /api/staff/login → 200"),                    // signing in is not an outcome
            Step(2, "fill"),
            Step(3, "click", "POST /api/bookings → 201", "GET /api/bookings → 200"),
            Step(4, "click", "POST /api/bookings → 201"),                     // the same outcome again: the first, shorter path is kept
            new() { N = 5, Ok = true, Driver = "Probe", Action = new HuntAction { Type = "request" }, Requests = ["POST /api/bookings/B-1001/cancel → 200"] },
            Step(6, "click", "POST /api/bookings/B-1002/cancel → 200"),
        ],
    };

    static readonly AppTwin Twin = new()
    {
        Features = [new TwinFeature { Name = "Book a stay", Endpoints = ["POST /api/bookings"], Stories = ["HOTEL-3"], ForRoles = ["guest"] },
                    new TwinFeature { Name = "Check guests in", Endpoints = ["POST /api/staff/bookings/{id}/checkin"], Stories = ["HOTEL-5"], ForRoles = ["front-desk"] }],
        Rules = [new TwinRule { On = "POST /api/bookings/{id}/cancel", Story = "HOTEL-4", Statement = "Only confirmed bookings are cancelled", Check = "x", Status = "accepted" }],
    };

    [Fact]
    public void A_hunt_yields_one_candidate_per_outcome_with_the_shortest_path_that_reached_it()
    {
        var mined = JourneyMiner.Mine(Hunt(), Twin);
        Assert.Equal(["POST /api/bookings", "POST /api/bookings/{id}/cancel"], mined.Select(j => j.Outcome));

        var book = mined[0];
        Assert.Equal(("Book a stay", "guest", 4), (book.Name, book.Role, book.Steps.Count)); // steps 0 to 3
        Assert.Equal(["HOTEL-3"], book.Stories);
        Assert.Equal("candidate", book.Status);

        var cancel = mined[1];
        Assert.DoesNotContain(cancel.Steps, s => s.Driver == "Probe"); // a probe is not something a user does
        Assert.Equal(["HOTEL-4"], cancel.Stories);                     // linked through the twin's rule on its endpoint
    }

    [Fact]
    public void Planned_journeys_are_the_features_no_journey_reaches_and_coverage_counts_proven_ones()
    {
        var mined = JourneyMiner.Mine(Hunt(), Twin);
        var planned = JourneyMiner.Planned(new Project { Id = "hotel" }, Twin, mined);
        var gap = Assert.Single(planned);
        Assert.Equal(("Check guests in", "front-desk", "planned"), (gap.Name, gap.Role, gap.Status));

        mined[0].Status = "proven";
        var k = new KnowledgeSnapshot { Stories = [new("HOTEL-3", "Correct price", "c", 1, "h", null, null), new("HOTEL-4", "Cancel", "c", 1, "h", null, null), new("HOTEL-5", "Desk", "c", 1, "h", null, null)] };
        var cov = Coverage.Stories(k, Twin, mined);
        Assert.Equal((true, true, false), (cov[0].Covered, cov[1].Covered, cov[2].Covered)); // a proven journey; a rule; nothing yet
        Assert.Equal(["Book a stay"], cov[0].ProvenJourneys);
        // An AI-drafted rule no one has reviewed covers nothing yet.
        Twin.Rules[0].Status = "inferred";
        Assert.False(Coverage.Stories(k, Twin, mined)[1].Covered);
        Twin.Rules[0].Status = "accepted";
    }
}
