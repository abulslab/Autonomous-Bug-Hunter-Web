using BugHunter.Api.Hunter;
using BugHunter.Api.Projects;
using BugHunter.Api.Twin;

namespace BugHunter.Tests;

public class TwinTests
{
    static readonly Project Hotel = new()
    {
        Id = "hotel",
        Roles = [new() { Id = "guest", Name = "Guest" }, new() { Id = "front-desk", Name = "Front desk", Secrets = new() { ["email"] = "desk-email" } }],
    };

    static (HuntStep, NetworkEvent) Call(int n, string method, string path, int status, string? body = null, bool dropAuth = false) =>
        (new HuntStep { N = n, Action = new HuntAction { Type = dropAuth ? "request" : "click", Args = dropAuth ? new() { ["dropAuth"] = "true" } : null } },
         new NetworkEvent { Method = method, Url = "http://hotel.test" + path, Status = status, ResourceType = "fetch", Body = body });

    static AppMap Map(params string[] states)
    {
        var m = new AppMap { Site = "http://hotel.test" };
        foreach (var s in states) { m.States.Add(new MapState { Id = s, Url = "http://hotel.test" + s, Title = "Demo Hotel" }); m.StatesThisRun.Add(s); }
        return m;
    }

    [Fact]
    public void Each_hunt_adds_a_version_credited_to_its_role_with_statuses_per_role()
    {
        var guestHunt = new Hunt { Id = "H-guest", Role = "guest", KnowledgeVersion = 1 };
        var v1 = TwinBuilder.Next(null, Hotel, guestHunt, Map("/", "/#/bookings"),
        [
            Call(1, "GET", "/api/rooms", 200, """{"items":[{"id":"r-101","rate":89}]}"""),
            Call(2, "GET", "/api/staff/bookings", 401),
            Call(3, "POST", "/api/bookings/B-1001/cancel", 401, dropAuth: true),
        ]);
        Assert.Equal(1, v1.Version);
        Assert.Equal(["/", "/#/bookings"], v1.States.Select(s => s.Id));
        Assert.Contains("items[].rate", v1.Endpoints.Single(e => e.Key == "GET /api/rooms").ResponseFields);
        Assert.Equal([401], v1.Endpoints.Single(e => e.Key == "POST /api/bookings/{id}/cancel").StatusesByRole[TwinBuilder.NoSession]);
        Assert.Contains(v1.Unknowns, u => u.Text == "Nothing has been observed as Front desk yet.");
        Assert.Contains(v1.Unknowns, u => u.Text.StartsWith("GET /api/staff/bookings has never succeeded"));

        var deskHunt = new Hunt { Id = "H-desk", Role = "front-desk", KnowledgeVersion = 1 };
        var v2 = TwinBuilder.Next(v1, Hotel, deskHunt, Map("/#/desk"),
        [
            Call(1, "GET", "/api/staff/bookings", 200, """{"items":[{"id":"B-1000","status":"Confirmed"}]}"""),
            Call(2, "POST", "/api/staff/bookings/B-1000/checkin", 200, """{"id":"B-1000","status":"CheckedIn","checkedInAt":"2026-11-03T10:00:00Z"}"""),
        ]);
        var desk = v2.Endpoints.Single(e => e.Key == "GET /api/staff/bookings");
        Assert.Equal([401], desk.StatusesByRole["guest"]); // the same endpoint, two roles
        Assert.Equal([200], desk.StatusesByRole["front-desk"]);
        Assert.Equal((1, 2), (desk.FirstSeen, desk.LastSeen));
        Assert.Equal(["POST /api/staff/bookings/{id}/checkin"], v2.Changes.EndpointsAdded);
        Assert.Contains("GET /api/staff/bookings 200 as front-desk", v2.Changes.StatusesAdded);
        Assert.DoesNotContain(v2.Unknowns, u => u.Kind == "role");
        Assert.DoesNotContain(v2.Unknowns, u => u.Text.StartsWith("GET /api/staff/bookings has never succeeded"));
        Assert.Equal("H-desk:2", v2.Endpoints.Single(e => e.Key == "POST /api/staff/bookings/{id}/checkin").Evidence[0].Ref);
        Assert.Equal(1, v1.Endpoints.Count(e => e.Key == "GET /api/staff/bookings") ); // the previous version is not changed
        Assert.False(v1.Endpoints.Single(e => e.Key == "GET /api/staff/bookings").StatusesByRole.ContainsKey("front-desk"));
    }
}
