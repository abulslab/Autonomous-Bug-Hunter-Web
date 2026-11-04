using System.Text.Json.Nodes;
using BugHunter.Api.Hunter;
using BugHunter.Api.Projects;
using BugHunter.Api.Twin;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BugHunter.Tests;

public class SpecTests
{
    static async Task<JsonNode> Spec<T>() where T : class
    {
        await using var f = new WebApplicationFactory<T>();
        return JsonNode.Parse(await f.CreateClient().GetStringAsync("/openapi/v1.json"))!;
    }

    [Fact]
    public async Task The_OpenAPI_reader_finds_every_operation_with_its_fields()
    {
        var hotel = Specs.OpenApi(await Spec<DemoHotel.Hotel>());
        Assert.Equal(10, hotel.Count);
        var book = hotel.Single(e => e is { Method: "POST", Path: "/api/bookings" });
        Assert.Contains("breakfast", book.RequestFields);
        Assert.Contains("total", book.ResponseFields);
        Assert.Contains(201, book.Statuses);
        Assert.Contains("items[].rate", hotel.Single(e => e.Path == "/api/rooms").ResponseFields);
        Assert.Contains(hotel, e => e.Path == "/api/staff/bookings/{id}/checkin");

        Assert.Equal(18, Specs.OpenApi(await Spec<DemoShop.SimStore>()).Count);
    }

    static readonly Project Hotel = new() { Id = "hotel", Roles = [new() { Id = "guest", Name = "Guest" }, new() { Id = "front-desk", Name = "Front desk" }] };

    [Fact]
    public void Declared_endpoints_become_observed_and_drift_is_named()
    {
        List<DeclaredEndpoint> spec =
        [
            new("GET", "/api/rooms", [200], [], ["items[].id", "items[].rate"], "Rooms"),
            new("POST", "/api/bookings/{id}/cancel", [200, 404, 409], [], ["status"], "Cancel"),
        ];
        var v1 = TwinBuilder.Declare(null, Hotel, spec, "openapi http://hotel.test/openapi/v1.json");
        Assert.All(v1.Endpoints, e => Assert.Equal("declared", e.Status));
        Assert.Equal(2, v1.Unknowns.Count(u => u.Kind == "coverage"));

        var hunt = new Hunt { Id = "H-1", Role = "guest" };
        var map = new AppMap();
        var v2 = TwinBuilder.Next(v1, Hotel, hunt, map,
        [
            (new HuntStep { N = 1, Action = new() { Type = "open" } }, new NetworkEvent { Method = "GET", Url = "http://hotel.test/api/rooms", Status = 200, ResourceType = "fetch", Body = """{"items":[{"id":"r-1","rate":89,"maxGuests":1}]}""" }),
            (new HuntStep { N = 2, Action = new() { Type = "click" } }, new NetworkEvent { Method = "GET", Url = "http://hotel.test/api/bookings", Status = 200, ResourceType = "fetch", Body = """{"items":[]}""" }),
        ]);
        var rooms = v2.Endpoints.Single(e => e.Key == "GET /api/rooms");
        Assert.Equal(("observed", true), (rooms.Status, rooms.Declared));
        Assert.Contains(v2.Unknowns, u => u.Kind == "coverage" && u.Text.StartsWith("POST /api/bookings/{id}/cancel"));
        Assert.Contains(v2.Unknowns, u => u.Kind == "undocumented" && u.Text.StartsWith("GET /api/bookings"));
        Assert.Contains(v2.Unknowns, u => u.Kind == "schema" && u.Text.Contains("items[].maxGuests"));
    }

    [Fact]
    public void A_HAR_file_adds_observations_and_the_matrix_shows_what_each_role_got()
    {
        var har = JsonNode.Parse("""
            {"log":{"entries":[
              {"request":{"method":"GET","url":"http://hotel.test/api/staff/bookings"},"response":{"status":401,"content":{"text":"{}"}},"_resourceType":"fetch"},
              {"request":{"method":"GET","url":"http://hotel.test/logo.png"},"response":{"status":200},"_resourceType":"image"}
            ]}}
            """)!;
        var events = Specs.Har(har);
        Assert.Single(events); // only API calls
        var v1 = TwinBuilder.ObserveHar(null, Hotel, events, "har support-case-17", role: "guest");
        var v2 = TwinBuilder.Next(v1, Hotel, new Hunt { Id = "H-2", Role = "front-desk" }, new AppMap(),
            [(new HuntStep { N = 1, Action = new() { Type = "click" } }, new NetworkEvent { Method = "GET", Url = "http://hotel.test/api/staff/bookings", Status = 200, ResourceType = "fetch", Body = "{}" })]);
        var row = Assert.Single(TwinBuilder.Permissions(v2, Hotel));
        Assert.Equal(("denied", "allowed", "unknown"), (row.Roles["guest"], row.Roles["front-desk"], row.Roles[TwinBuilder.NoSession]));
    }
}
