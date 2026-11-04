using System.Text.Json.Nodes;
using BugHunter.Api.Projects;
using BugHunter.Api.Twin;

namespace BugHunter.Tests;

public class UnderstandingTests
{
    static readonly Project Hotel = new() { Id = "hotel", Roles = [new() { Id = "guest", Name = "Guest" }, new() { Id = "front-desk", Name = "Front desk" }] };

    static AppTwin Twin()
    {
        var t = new AppTwin { ProjectId = "hotel", Version = 4, States = [new TwinState { Id = "/#/bookings" }, new TwinState { Id = "/" }] };
        t.Endpoints.Add(new TwinEndpoint { Method = "POST", Path = "/api/bookings", Status = "observed", RequestFields = ["roomId", "guests", "checkIn"], ResponseFields = ["id", "nights", "rate", "total", "guests", "maxGuests", "status"], StatusesByRole = new() { ["guest"] = [201] } });
        t.Endpoints.Add(new TwinEndpoint { Method = "POST", Path = "/api/bookings/{id}/cancel", Status = "declared", Declared = true, DeclaredResponseFields = ["status", "checkedInAt"] });
        return t;
    }

    static readonly KnowledgeSnapshot Knowledge = new() { Stories = [new("HOTEL-3", "Correct price", "The total is nights times the rate.", 1, "h", null, null)] };

    [Fact]
    public void The_verifier_keeps_what_the_twin_supports_and_says_why_it_refuses_the_rest()
    {
        var reply = JsonNode.Parse("""
            {
              "entities": [
                { "name": "Booking", "fields": ["id", "total", "nights", "loyaltyPoints"], "endpoints": ["POST /api/bookings"] },
                { "name": "Invoice", "fields": ["invoiceNumber"], "endpoints": [] }
              ],
              "features": [
                { "name": "Book a stay", "pages": ["#/book/{roomId}"], "endpoints": ["POST /api/bookings"], "stories": ["HOTEL-3", "HOTEL-99"], "roles": ["guest", "admin"] },
                { "name": "Loyalty", "pages": ["#/loyalty"], "endpoints": ["GET /api/loyalty"] }
              ],
              "lifecycles": [
                { "entity": "Booking", "field": "status", "states": ["Confirmed", "Cancelled"],
                  "transitions": [{ "from": "Confirmed", "to": "Cancelled", "endpoint": "POST /api/bookings/B-1/cancel", "role": "guest" },
                                  { "from": "Confirmed", "to": "Refunded", "endpoint": "POST /api/refunds" }] }
              ],
              "rules": [
                { "statement": "Every night is charged", "story": "HOTEL-3", "on": "POST /api/bookings", "check": "status >= 400 || total == nights * rate" },
                { "statement": "Discount is capped", "story": "HOTEL-3", "on": "POST /api/bookings", "check": "discount <= total" },
                { "statement": "Broken", "on": "POST /api/bookings", "check": "total ==" },
                { "statement": "Only a confirmed booking can be cancelled", "story": "HOTEL-3", "on": "POST /api/bookings/{id}/cancel", "check": "status >= 400 || !exists(checkedInAt)" }
              ]
            }
            """)!.AsObject();
        var t = Twin();
        var (proposed, admitted) = Understanding.Verify(t, Hotel, Knowledge, reply);
        Assert.Equal((9, 5), (proposed, admitted));

        var booking = Assert.Single(t.Entities);
        Assert.Equal(["id", "total", "nights"], booking.Fields); // the invented field is dropped
        Assert.Equal("verified", booking.Status);                // everything it rests on was observed

        var feature = Assert.Single(t.Features);
        Assert.Equal(["HOTEL-3"], feature.Stories); // the unknown story and role are dropped
        Assert.Equal(["guest"], feature.ForRoles);
        Assert.Equal("verified", feature.Status);

        var lifecycle = Assert.Single(t.Lifecycles);
        Assert.Equal(["POST /api/bookings/{id}/cancel"], lifecycle.Transitions.Select(x => x.Endpoint));
        Assert.Equal("inferred", lifecycle.Status); // its only transition is declared, never observed

        Assert.Equal(2, t.Rules.Count);
        var rule = t.Rules[0];
        Assert.Equal(("POST /api/bookings", "HOTEL-3", "inferred"), (rule.On, rule.Story, rule.Status));
        Assert.Equal("POST /api/bookings/{id}/cancel", t.Rules[1].On); // a {placeholder} path matches the folded endpoint

        Assert.Contains(t.Rejected, r => r is { Kind: "entity", Name: "Invoice" });
        Assert.Contains(t.Rejected, r => r is { Kind: "feature", Name: "Loyalty" });
        Assert.Contains(t.Rejected, r => r.Kind == "rule" && r.Reason.Contains("discount"));
        Assert.Contains(t.Rejected, r => r.Kind == "rule" && r.Reason.StartsWith("not a valid check"));
    }

    [Fact]
    public void Identifiers_are_field_names_without_keywords_functions_or_literals()
    {
        Assert.Equal(["total", "nights", "rate"], Understanding.Identifiers("status >= 400 || total == nights * rate"));
        Assert.Equal(["lineTotal".ToLowerInvariant(), "price", "qty"], Understanding.Identifiers("all(items, lineTotal == price * qty)"));
        Assert.Equal(["qty"], Understanding.Identifiers("request.qty >= 1 && lower('A b') == 'a b'"));
    }
}
