using BugHunter.Api.Hunter;
using BugHunter.Api.Runs;
using BugHunter.Api.Twin;

namespace BugHunter.Tests;

public class RuleAndRunTests
{
    static NetworkEvent E(string method, string path, string body, int status = 200) =>
        new() { Method = method, Url = "http://hotel.test" + path, Status = status, ResourceType = "fetch", Body = body };

    [Fact]
    public void Prev_is_the_same_entity_as_it_was_last_seen_not_the_last_call_to_the_endpoint()
    {
        var rule = InvariantRules.Create(null, "POST /api/staff/bookings/{id}/checkout", "prev.status == \"CheckedIn\"", "checked in before checked out", null, "Twin", 0).Rule!;
        var history = new List<NetworkEvent>
        {
            E("GET", "/api/staff/bookings", """{"items":[{"id":"B-1","status":"Confirmed"},{"id":"B-2","status":"Confirmed"}]}"""),
            E("POST", "/api/staff/bookings/B-1/checkin", """{"id":"B-1","status":"CheckedIn"}"""),
            E("POST", "/api/staff/bookings/B-1/checkout", """{"id":"B-1","status":"CheckedOut"}"""),
            E("POST", "/api/staff/bookings/B-2/checkin", """{"id":"B-2","status":"CheckedIn"}"""),
        };
        var checkout2 = E("POST", "/api/staff/bookings/B-2/checkout", """{"id":"B-2","status":"CheckedOut"}""");

        // B-2 was checked in before its checkout: the rule holds. (Before, prev was B-1's checkout, and it failed.)
        Assert.True(InvariantRules.EvaluateWith(rule, checkout2, InvariantRules.Prior(history, rule, checkout2)).Holds);
        // A checkout of a booking only ever seen Confirmed (in the list) breaks it: the real defect.
        var early = E("POST", "/api/staff/bookings/B-2/checkout", """{"id":"B-2","status":"CheckedOut"}""");
        Assert.False(InvariantRules.EvaluateWith(rule, early, InvariantRules.Prior(history.Take(1).ToList(), rule, early)).Holds);
        // Never seen before: skipped, not failed.
        var unknown = E("POST", "/api/staff/bookings/B-9/checkout", """{"id":"B-9","status":"CheckedOut"}""");
        Assert.Null(InvariantRules.EvaluateWith(rule, unknown, InvariantRules.Prior(history, rule, unknown)).Holds);
    }

    [Fact]
    public void The_same_check_in_other_words_is_one_rule()
    {
        Assert.True(Understanding.SameCheck("guests >= 1 && guests <= maxGuests", "status >= 400 || (guests >= 1 && guests <= maxGuests)"));
        Assert.True(Understanding.SameCheck("prev.status == 'CheckedIn'", "prev.status == \"CheckedIn\""));
        Assert.False(Understanding.SameCheck("total == nights * rate", "total == nights * rate + breakfastTotal"));
        Assert.False(Understanding.SameCheck("(a > 1) || (b > 1)", "a > 1) || (b > 1"));
    }

    [Fact]
    public void Gone_is_fixed_only_where_the_run_tried_again_and_a_release_is_every_run_since_the_baseline()
    {
        KnownFinding F(string title, string ep, string role) => new(title, "Prediction", "Confirmed", "H-1", "F1", ep, role);
        var baseline = new RunSnapshot
        {
            Findings = { ["desk"] = F("Desk open to guests", "GET /api/staff/bookings", "guest"), ["total"] = F("Last night free", "POST /api/bookings", "guest"),
                         ["checkin"] = F("Check-in open to guests", "POST /api/staff/bookings/{id}/checkin", "guest") },
        };
        var hunt = new RunSnapshot { Findings = { ["desk"] = F("Desk open to guests", "GET /api/staff/bookings", "guest") }, Exercised = ["guest GET /api/staff/bookings", "guest POST /api/bookings"] };
        var c = RunCompare.Compare(baseline, hunt, null, "R-1");
        Assert.Equal(["Last night free"], c.FindingsFixed.Select(f => f.Title));             // booked again, the bug did not show
        Assert.Equal(["Check-in open to guests"], c.FindingsNotRetested.Select(f => f.Title)); // never tried: not fixed
        Assert.False(c.OnlyImprovements); // a run that covered less never becomes the baseline by itself

        var survey = new RunSnapshot { Findings = { ["rate"] = F("Rate differs", "POST /api/bookings", "guest") }, Journeys = { ["book"] = "proven" }, Exercised = ["front-desk GET /api/staff/bookings"] };
        var merged = RunCompare.Merge([hunt, survey]);
        Assert.Equal(["desk", "rate"], merged.Findings.Keys.Order());                         // both runs' bugs are open
        Assert.Equal(3, merged.Exercised.Count);
        Assert.Equal("proven", merged.Journeys["book"]);
    }
}
