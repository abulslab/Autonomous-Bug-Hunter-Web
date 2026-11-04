using BugHunter.Api.Hunter;

namespace BugHunter.Tests;

public class RuleAdmissionTests
{
    static (Hunt, HuntContext) Seen(params string[] calls)
    {
        var h = new Hunt { Id = "H-test", Url = "http://hotel.test/" };
        var ctx = new HuntContext { Hunt = h, Page = new PageSnapshot(), Oracles = new OracleState() };
        foreach (var c in calls)
        {
            var (method, path) = (c.Split(' ')[0], c.Split(' ')[1]);
            ctx.Oracles.History.Add((new HuntStep(), new NetworkEvent { Method = method, Url = "http://hotel.test" + path, Status = 200, ResourceType = "fetch" }));
            h.Coverage.Endpoints.Add(c);
        }
        return (h, ctx);
    }

    static Invariant Rule(string on, string check) => InvariantRules.Create(null, on, check, "claim", null, "Model", 1).Rule!;

    [Fact]
    public void A_rule_for_an_endpoint_not_reached_yet_waits_for_it_under_the_same_api()
    {
        // Planned after a survey that only searched: the booking endpoint is real but not recorded yet.
        var (h, ctx) = Seen("GET /api/rooms");
        Assert.Null(ModelHuntPolicy.Admit(h, ctx, Rule("POST /api/bookings", "status >= 400 || total == nights * rate")));
        Assert.True(h.Invariants.Single().Deferred);

        // A path under an API the app never used is a guess, and the model is told what exists instead.
        var why = ModelHuntPolicy.Admit(h, ctx, Rule("POST /v2/orders", "total > 0"));
        Assert.Contains("matches no API call seen so far", why);
        Assert.Single(h.Invariants);
    }

    [Fact]
    public void A_rule_for_a_recorded_endpoint_is_admitted_at_once()
    {
        var (h, ctx) = Seen("GET /api/rooms");
        Assert.Null(ModelHuntPolicy.Admit(h, ctx, Rule("GET /api/rooms", "status == 200")));
        Assert.False(h.Invariants.Single().Deferred);
    }
}

public class BenchmarkScoringTests
{
    static Invariant Rule(string on, string check) => InvariantRules.Create(null, on, check, "claim", null, "Model", 1).Rule!;

    [Fact]
    public void A_broken_rule_is_credited_only_to_the_defects_it_checks()
    {
        // One bad booking can show several seeded defects; the capacity rule found the capacity bug only.
        var capacity = Rule("POST /api/bookings", "status >= 400 || guests <= maxGuests");
        Assert.True(HuntEngine.About(capacity, "over-capacity"));
        Assert.False(HuntEngine.About(capacity, "past-checkin"));
        Assert.False(HuntEngine.About(capacity, "checkout-before-checkin"));

        var price = Rule("POST /api/bookings", "status >= 400 || total == nights * rate");
        Assert.True(HuntEngine.About(price, "wrong-total"));
        Assert.False(HuntEngine.About(price, "past-checkin"));
        Assert.False(HuntEngine.About(price, "quote-mismatch")); // a rate that differs from the listing needs a cross-endpoint check
    }
}

public class SeededDefectScoringTests
{
    static NetworkEvent Event(string method, string path, int status, string body, string? request = null) =>
        new() { Method = method, Url = "http://hotel.test" + path, Status = status, ResourceType = "fetch", Body = body, RequestBody = request };

    static OracleState Recorded(params NetworkEvent[] events)
    {
        var st = new OracleState();
        foreach (var e in events) st.History.Add((new HuntStep(), e));
        return st;
    }

    [Fact]
    public void A_booking_charged_above_the_listed_rate_shows_the_quote_defect()
    {
        var rooms = Event("GET", "/api/rooms", 200, """{"items":[{"id":"r-301","rate":459}]}""");
        var booked = Event("POST", "/api/bookings", 201, """{"roomId":"r-301","rate":499,"nights":1,"total":499}""");
        Assert.True(HuntEngine.Exhibits("quote-mismatch", booked, Recorded(rooms, booked)));
        var fair = Event("POST", "/api/bookings", 201, """{"roomId":"r-301","rate":459,"nights":1,"total":459}""");
        Assert.False(HuntEngine.Exhibits("quote-mismatch", fair, Recorded(rooms, fair)));
    }

    [Fact]
    public void Front_desk_data_without_a_front_desk_sign_in_shows_the_permission_defect()
    {
        var list = Event("GET", "/api/staff/bookings", 200, """{"items":[]}""");
        Assert.True(HuntEngine.Exhibits("desk-open", list, Recorded(list)));
        var login = Event("POST", "/api/staff/login", 200, """{"token":"t","name":"Front desk"}""");
        var signedIn = Event("GET", "/api/staff/bookings", 200, """{"items":[]}""");
        Assert.False(HuntEngine.Exhibits("desk-open", signedIn, Recorded(login, signedIn)));
        var probe = new HuntStep { Action = new HuntAction { Type = "request", Args = new() { ["dropAuth"] = "true" } } };
        Assert.True(HuntEngine.Exhibits("desk-open", signedIn, Recorded(login, signedIn), probe)); // the same call resent without the session
    }

    [Fact]
    public void Cancelling_a_shipped_order_shows_the_lifecycle_defect()
    {
        var cancelled = Event("POST", "/api/orders/ORD-1001/cancel", 200, """{"id":"ORD-1001","status":"Cancelled","deliveredAt":"2026-10-25T00:00:00Z"}""");
        Assert.True(HuntEngine.Exhibits("cancel-shipped", cancelled, Recorded(cancelled)));
        var early = Event("POST", "/api/orders/ORD-1002/cancel", 200, """{"id":"ORD-1002","status":"Cancelled"}""");
        Assert.False(HuntEngine.Exhibits("cancel-shipped", early, Recorded(early)));
    }
}
