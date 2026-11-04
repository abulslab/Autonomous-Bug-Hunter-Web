using BugHunter.Api.Runs;
using BugHunter.Api.Twin;

namespace BugHunter.Tests;

public class RunTests
{
    static KnownFinding F(string title) => new(title, "Prediction", "Confirmed", "H-1", "F1");

    [Fact]
    public void A_run_is_compared_with_the_baseline_finding_by_finding_and_journey_by_journey()
    {
        var baseline = new RunSnapshot
        {
            Findings = { ["prediction:permission:GET /api/staff/bookings"] = F("Front desk open"), ["invariant:price"] = F("Last night free") },
            Journeys = { ["guest-book"] = "proven", ["guest-cancel"] = "failing" },
            Behaviour = { ["POST /api/bookings"] = "guest 201 | id,total", ["GET /api/rooms"] = "guest 200 | items[].rate", ["GET /api/old"] = "guest 200 | x" },
            StoryVersions = { ["HOTEL-3"] = 1, ["HOTEL-1"] = 1 },
            StoriesCovered = ["HOTEL-1", "HOTEL-3"],
        };
        var current = new RunSnapshot
        {
            Findings = { ["prediction:permission:GET /api/staff/bookings"] = F("Front desk open"), ["prediction:consistency:POST /api/bookings:rate"] = F("Rate differs") },
            Journeys = { ["guest-book"] = "failing", ["guest-cancel"] = "proven", ["desk-checkin"] = "proven" },
            Behaviour = { ["POST /api/bookings"] = "guest 201 | breakfast,id,total", ["GET /api/rooms"] = "guest 200 | items[].breakfastRate,items[].rate", ["GET /api/staff/bookings"] = "guest 200 | items" },
            StoryVersions = { ["HOTEL-3"] = 2, ["HOTEL-1"] = 1 },
            StoriesCovered = ["HOTEL-3"],
        };
        var twin = new AppTwin { Features = [new TwinFeature { Name = "Book", Endpoints = ["POST /api/bookings"], Stories = ["HOTEL-3"] }] };

        var c = RunCompare.Compare(baseline, current, twin, "R-1");
        Assert.Equal(["Rate differs"], c.FindingsNew.Select(f => f.Title));
        Assert.Equal(["Last night free"], c.FindingsFixed.Select(f => f.Title));
        Assert.Equal(["Front desk open"], c.FindingsStillOpen.Select(f => f.Title));
        Assert.Equal(["guest-book: failing"], c.JourneysRegressed);
        Assert.Equal(["guest-cancel"], c.JourneysRecovered);
        Assert.Equal(["desk-checkin"], c.JourneysNew);
        Assert.Equal(["HOTEL-3 v1 -> v2"], c.StoriesChanged);

        var book = c.BehaviourChanged.Single(b => b.Endpoint == "POST /api/bookings");
        Assert.Equal(["HOTEL-3"], book.ExplainedBy);                                                   // the changed story explains it
        Assert.Empty(c.BehaviourChanged.Single(b => b.Endpoint == "GET /api/rooms").ExplainedBy);   // a change no story explains
        Assert.Equal(["GET /api/staff/bookings"], c.EndpointsAdded);
        Assert.Equal(["GET /api/old"], c.EndpointsRemoved);
        Assert.Equal(["HOTEL-1"], c.CoverageLost);

        Assert.True(RunCompare.Compare(current, current, twin, "R-2").Empty);
        Assert.True(RunCompare.Compare(null, current, twin, null).Empty); // the first run has nothing to compare with
    }
}
