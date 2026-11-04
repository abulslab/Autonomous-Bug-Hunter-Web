using BugHunter.Api.Hunter;
using BugHunter.Api.Projects;
using BugHunter.Api.Twin;

namespace BugHunter.Tests;

public class PredictionTests
{
    static NetworkEvent E(string method, string path, int status, string body, string? request = null) =>
        new() { Method = method, Url = "http://hotel.test" + path, Status = status, ResourceType = "fetch", Body = body, RequestBody = request, Rid = 1 };

    static readonly Project Hotel = new()
    {
        Id = "hotel",
        Roles = [new() { Id = "guest", Name = "Guest" }, new() { Id = "front-desk", Name = "Front desk", Exclusive = ["/api/staff/"] }],
    };

    static readonly AppTwin Twin = new()
    {
        Lifecycles = [new TwinLifecycle { Entity = "Booking", Field = "status", Transitions = [new("Confirmed", "Cancelled", "POST /api/bookings/{id}/cancel", "guest")] }],
        Endpoints = [new TwinEndpoint { Method = "GET", Path = "/api/staff/bookings" }, new TwinEndpoint { Method = "POST", Path = "/api/staff/login" }],
    };

    [Fact]
    public void Each_prediction_fires_only_on_what_contradicts_intent()
    {
        var ps = PredictionSet.For(Hotel, Twin, "guest");
        var st = new PredictionState();
        Assert.Empty(Predictions.Check(ps, st, E("GET", "/api/rooms", 200, """{"items":[{"id":"r-301","rate":459},{"id":"r-102","rate":129}]}"""), false));

        // Consistency: booked at another rate than listed.
        var quote = Predictions.Check(ps, st, E("POST", "/api/bookings", 201, """{"id":"B-1","roomId":"r-301","rate":499,"status":"CheckedIn"}"""), false);
        Assert.Equal("consistency", Assert.Single(quote).Kind);
        Assert.Empty(Predictions.Check(ps, st, E("POST", "/api/bookings", 201, """{"id":"B-2","roomId":"r-102","rate":129,"status":"Confirmed"}"""), false));

        // Lifecycle: cancelling a checked-in booking; cancelling a confirmed one is fine.
        Assert.Equal("lifecycle", Assert.Single(Predictions.Check(ps, st, E("POST", "/api/bookings/B-1/cancel", 200, """{"id":"B-1","status":"Cancelled"}"""), false)).Kind);
        Assert.Empty(Predictions.Check(ps, st, E("POST", "/api/bookings/B-2/cancel", 200, """{"id":"B-2","status":"Cancelled"}"""), false));

        // Permission: the front desk's area answering a guest, or a call without a session; signing in is never one.
        Assert.Equal("permission", Assert.Single(Predictions.Check(ps, st, E("GET", "/api/staff/bookings", 200, """{"items":[]}"""), false)).Kind);
        Assert.Empty(Predictions.Check(ps, st, E("GET", "/api/staff/bookings", 401, "{}"), false));
        Assert.Empty(Predictions.Check(ps, st, E("POST", "/api/staff/login", 200, """{"token":"t"}"""), false));
        var desk = PredictionSet.For(Hotel, Twin, "front-desk");
        Assert.Empty(Predictions.Check(desk, new PredictionState(), E("GET", "/api/staff/bookings", 200, """{"items":[]}"""), false));
        Assert.Single(Predictions.Check(desk, new PredictionState(), E("GET", "/api/staff/bookings", 200, """{"items":[]}"""), dropAuth: true));
    }

    [Fact]
    public void The_predictions_make_probes_for_other_items_other_roles_and_disallowed_transitions()
    {
        var ps = PredictionSet.For(Hotel, Twin, "guest");
        var st = new PredictionState();
        var rooms = E("GET", "/api/rooms", 200, """{"items":[{"id":"r-301","rate":459},{"id":"r-102","rate":129}]}""");
        var booking = E("POST", "/api/bookings", 201, """{"id":"B-1","roomId":"r-102","rate":129,"status":"CheckedIn"}""", """{"roomId":"r-102","guests":1}""");
        Predictions.Check(ps, st, rooms, false); Predictions.Check(ps, st, booking, false);
        var bases = new List<(HuntStep, NetworkEvent)> { (new HuntStep { N = 1 }, rooms), (new HuntStep { N = 2 }, booking) };

        var other = Assert.Single(Predictions.OtherItemProbes(st, bases));
        Assert.Contains("\"roomId\":\"r-301\"", other.Body);

        var cross = Assert.Single(Predictions.CrossRoleProbes(ps, Twin, bases)); // not the sign-in endpoint
        Assert.Equal(("/api/staff/bookings", "GET"), (cross.Url, cross.Method));

        var life = Assert.Single(Predictions.LifecycleProbes(ps, st, bases));
        Assert.Equal(("/api/bookings/B-1/cancel", "POST"), (life.Url, life.Method)); // B-1 is CheckedIn, not Confirmed
    }
}
