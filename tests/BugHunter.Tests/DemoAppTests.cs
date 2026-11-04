using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BugHunter.Tests;

// The demo apps are the ground truth every benchmark is scored against, so their behaviour is tested like product code.
public class DemoHotelTests(WebApplicationFactory<DemoHotel.Hotel> f) : IClassFixture<WebApplicationFactory<DemoHotel.Hotel>>
{
    static object Stay(int from, int to, int guests = 2, string room = "r-102", bool? breakfast = null) => new
    {
        roomId = room, checkIn = DateTime.UtcNow.AddDays(from).ToString("yyyy-MM-dd"), checkOut = DateTime.UtcNow.AddDays(to).ToString("yyyy-MM-dd"),
        guests, name = "Test Guest", email = "guest@example.test", breakfast
    };

    HttpClient Guest(string id)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Guest", id);
        return c;
    }

    async Task<HttpClient> Harness(Dictionary<string, bool>? defects = null, Dictionary<string, bool>? features = null)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new("Bearer", TestKeys.Demo);
        await c.PostAsync("/api/test/reset-all", null);
        if (defects is not null) await c.PutAsJsonAsync("/api/admin/defects", defects);
        if (features is not null) await c.PutAsJsonAsync("/api/test/features", features);
        return c;
    }

    static async Task<JsonNode> Json(HttpResponseMessage r) => JsonNode.Parse(await r.Content.ReadAsStringAsync())!;

    [Fact]
    public async Task Business_rules_hold_until_a_defect_is_switched_on()
    {
        var harness = await Harness();
        var g = Guest("guest-rules-0001");
        Assert.Equal(HttpStatusCode.BadRequest, (await g.PostAsJsonAsync("/api/bookings", Stay(-2, 1))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await g.PostAsJsonAsync("/api/bookings", Stay(5, 5))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await g.PostAsJsonAsync("/api/bookings", Stay(5, 7, guests: 3))).StatusCode);
        Assert.Equal(4 * 129m, (await Json(await g.PostAsJsonAsync("/api/bookings", Stay(5, 9))))["total"]!.GetValue<decimal>());
        Assert.Equal(HttpStatusCode.BadRequest, (await f.CreateClient().PostAsJsonAsync("/api/bookings", Stay(5, 9))).StatusCode); // no guest header

        await harness.PutAsJsonAsync("/api/admin/defects", new Dictionary<string, bool> { ["over-capacity"] = true, ["wrong-total"] = true, ["quote-mismatch"] = true });
        Assert.Equal(HttpStatusCode.Created, (await g.PostAsJsonAsync("/api/bookings", Stay(5, 7, guests: 3))).StatusCode);
        Assert.Equal(3 * 129m, (await Json(await g.PostAsJsonAsync("/api/bookings", Stay(5, 9))))["total"]!.GetValue<decimal>()); // last night free
        Assert.Equal(499m, (await Json(await g.PostAsJsonAsync("/api/bookings", Stay(5, 6, room: "r-301"))))["rate"]!.GetValue<decimal>()); // listed at 459
    }

    [Fact]
    public async Task Bookings_are_private_and_only_the_front_desk_moves_them_through_their_lifecycle()
    {
        var harness = await Harness();
        var ana = Guest("guest-ana-0001"); var ben = Guest("guest-ben-0001");
        var id = (await Json(await ana.PostAsJsonAsync("/api/bookings", Stay(3, 5))))["id"]!.GetValue<string>();
        Assert.Single((await Json(await ana.GetAsync("/api/bookings")))["items"]!.AsArray());
        Assert.Empty((await Json(await ben.GetAsync("/api/bookings")))["items"]!.AsArray());
        Assert.Equal(HttpStatusCode.NotFound, (await ben.PostAsync($"/api/bookings/{id}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ben.GetAsync("/api/staff/bookings")).StatusCode);

        var desk = f.CreateClient();
        var token = (await Json(await desk.PostAsJsonAsync("/api/staff/login", new { email = "desk@hotel.demo", password = "Desk123!" })))["token"]!.GetValue<string>();
        desk.DefaultRequestHeaders.Add("X-Staff-Session", token);
        Assert.Equal(2, (await Json(await desk.GetAsync("/api/staff/bookings")))["items"]!.AsArray().Count); // the seeded booking and Ana's
        Assert.Equal(HttpStatusCode.Conflict, (await desk.PostAsync($"/api/staff/bookings/{id}/checkout", null)).StatusCode); // not checked in yet
        Assert.Equal("CheckedIn", (await Json(await desk.PostAsync($"/api/staff/bookings/{id}/checkin", null)))["status"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Conflict, (await ana.PostAsync($"/api/bookings/{id}/cancel", null)).StatusCode);

        await harness.PutAsJsonAsync("/api/admin/defects", new Dictionary<string, bool> { ["desk-open"] = true, ["cancel-after-checkin"] = true });
        Assert.Equal(HttpStatusCode.OK, (await ben.GetAsync("/api/staff/bookings")).StatusCode);
        Assert.Equal("Cancelled", (await Json(await ana.PostAsync($"/api/bookings/{id}/cancel", null)))["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Scenario_changes_edit_stories_and_switch_features_on()
    {
        var harness = await Harness(features: new() { ["breakfast"] = true });
        var story = await Json(await harness.PutAsJsonAsync("/api/test/stories/HOTEL-3", new { criteria = "Breakfast included when chosen." }));
        Assert.Equal(2, story["version"]!.GetValue<int>());
        Assert.Equal(15m, (await Json(await f.CreateClient().GetAsync("/api/rooms")))["items"]![0]!["breakfastRate"]!.GetValue<decimal>());

        var g = Guest("guest-breakfast-01");
        var b = await Json(await g.PostAsJsonAsync("/api/bookings", Stay(3, 6, breakfast: true))); // 3 nights, 2 guests
        Assert.Equal(90m, b["breakfastTotal"]!.GetValue<decimal>());
        Assert.Equal(3 * 129m + 90m, b["total"]!.GetValue<decimal>());

        await harness.PutAsJsonAsync("/api/admin/defects", new Dictionary<string, bool> { ["breakfast-once"] = true });
        Assert.Equal(30m, (await Json(await g.PostAsJsonAsync("/api/bookings", Stay(3, 6, breakfast: true))))["breakfastTotal"]!.GetValue<decimal>());

        await harness.PostAsync("/api/test/reset-all", null);
        var stories = await Json(await harness.GetAsync("/api/stories"));
        Assert.All(stories.AsArray(), s => Assert.Equal(1, s!["version"]!.GetValue<int>()));
        Assert.Null((await Json(await f.CreateClient().GetAsync("/api/rooms")))["items"]![0]!["breakfastRate"]);
    }
}

public class DemoShopLifecycleTests(WebApplicationFactory<DemoShop.SimStore> f) : IClassFixture<WebApplicationFactory<DemoShop.SimStore>>
{
    static async Task<JsonNode> Json(HttpResponseMessage r) => JsonNode.Parse(await r.Content.ReadAsStringAsync())!;

    async Task<HttpClient> SignIn(string email, string password)
    {
        var c = f.CreateClient();
        var token = (await Json(await c.PostAsJsonAsync("/api/login", new { email, password })))["token"]!.GetValue<string>();
        c.DefaultRequestHeaders.Add("X-Sim-Session", token);
        return c;
    }

    async Task<string> Buy(HttpClient shopper, bool? giftWrap = null)
    {
        await shopper.PostAsJsonAsync("/api/cart/items", new { productId = "p-102", qty = 2 });
        var order = await shopper.PostAsJsonAsync("/api/checkout", new { name = "Test Shopper", address = "1 Test Street", city = "Springfield", zip = "12345", cardNumber = "4242 4242 4242 4242", expiry = "12/30", cvv = "123", giftWrap });
        Assert.Equal(HttpStatusCode.Created, order.StatusCode);
        return (await Json(order))["id"]!.GetValue<string>();
    }

    [Fact]
    public async Task Only_staff_ship_orders_and_shoppers_cancel_only_before_shipping()
    {
        var harness = f.CreateClient(); harness.DefaultRequestHeaders.Authorization = new("Bearer", TestKeys.Demo);
        await harness.PostAsync("/api/test/reset-all", null);
        var shopper = await SignIn("shopper01@shop.demo", "Shop123!");
        var staff = await SignIn("staff@shop.demo", "Staff123!");
        Assert.Equal("staff", (await Json(await staff.GetAsync("/api/me")))["role"]!.GetValue<string>());

        var shipped = await Buy(shopper);
        Assert.Equal(HttpStatusCode.Forbidden, (await shopper.GetAsync("/api/staff/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostAsJsonAsync($"/api/staff/orders/{shipped}/status", new { status = "Delivered" })).StatusCode); // must ship first
        Assert.Equal("Shipped", (await Json(await staff.PostAsJsonAsync($"/api/staff/orders/{shipped}/status", new { status = "Shipped" })))["status"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Conflict, (await shopper.PostAsync($"/api/orders/{shipped}/cancel", null)).StatusCode);

        var stockBefore = (await Json(await f.CreateClient().GetAsync("/api/products")))["items"]!.AsArray().First(p => p!["id"]!.GetValue<string>() == "p-102")!["stock"]!.GetValue<int>();
        var open = await Buy(shopper);
        Assert.Equal("Cancelled", (await Json(await shopper.PostAsync($"/api/orders/{open}/cancel", null)))["status"]!.GetValue<string>());
        var stockAfter = (await Json(await f.CreateClient().GetAsync("/api/products")))["items"]!.AsArray().First(p => p!["id"]!.GetValue<string>() == "p-102")!["stock"]!.GetValue<int>();
        Assert.Equal(stockBefore, stockAfter); // the cancelled order's items are back in stock

        await harness.PutAsJsonAsync("/api/admin/defects", new Dictionary<string, bool> { ["staff-open"] = true, ["cancel-shipped"] = true });
        Assert.Equal(HttpStatusCode.OK, (await shopper.GetAsync("/api/staff/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await shopper.PostAsync($"/api/orders/{shipped}/cancel", null)).StatusCode);
    }

    [Fact]
    public async Task Gift_wrap_is_charged_once_per_order_unless_the_defect_is_on()
    {
        var harness = f.CreateClient(); harness.DefaultRequestHeaders.Authorization = new("Bearer", TestKeys.Demo);
        await harness.PostAsync("/api/test/reset-all", null);
        Assert.Null((await Json(await f.CreateClient().GetAsync("/api/checkout/options")))["giftWrapPrice"]);
        await harness.PutAsJsonAsync("/api/test/features", new Dictionary<string, bool> { ["gift-wrap"] = true });
        Assert.Equal(4.99m, (await Json(await f.CreateClient().GetAsync("/api/checkout/options")))["giftWrapPrice"]!.GetValue<decimal>());

        var shopper = await SignIn("shopper01@shop.demo", "Shop123!");
        var once = await Json(await shopper.GetAsync($"/api/orders/{await Buy(shopper, giftWrap: true)}"));
        Assert.Equal(4.99m, once["giftWrapFee"]!.GetValue<decimal>());

        await harness.PutAsJsonAsync("/api/admin/defects", new Dictionary<string, bool> { ["gift-wrap-per-item"] = true, ["listing-price-stale"] = true });
        var perItem = await Json(await shopper.GetAsync($"/api/orders/{await Buy(shopper, giftWrap: true)}"));
        Assert.Equal(2 * 4.99m, perItem["giftWrapFee"]!.GetValue<decimal>());
        Assert.Equal(119.00m, (await Json(await f.CreateClient().GetAsync("/api/products")))["items"]!.AsArray().First(p => p!["id"]!.GetValue<string>() == "p-108")!["price"]!.GetValue<decimal>());
    }
}

// The answer keys must describe the apps as they are: every endpoint in an answer key is in that app's OpenAPI document
// and the other way round, and the test harness stays out of the product's API description.
public class AnswerKeyTests
{
    static string Root([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    static async Task AssertMatches<T>(string app) where T : class
    {
        await using var factory = new WebApplicationFactory<T>();
        var spec = JsonNode.Parse(await factory.CreateClient().GetStringAsync("/openapi/v1.json"))!;
        var served = spec["paths"]!.AsObject().SelectMany(p => p.Value!.AsObject().Select(m => $"{m.Key.ToUpperInvariant()} {p.Key}")).ToHashSet();
        var key = JsonNode.Parse(File.ReadAllText(Path.Combine(Root(), "ground-truth", app, "answer-key.json")))!;
        var listed = key["endpoints"]!.AsArray().Select(e => $"{e!["method"]} {e["path"]}").ToHashSet();

        Assert.Empty(listed.Except(served));
        Assert.Empty(served.Except(listed));
        Assert.DoesNotContain(served, e => e.Contains("/admin/") || e.Contains("/test/") || e.EndsWith("/stories"));
    }

    [Fact] public Task Hotel_answer_key_matches_its_OpenAPI_document() => AssertMatches<DemoHotel.Hotel>("hotel");
    [Fact] public Task Shop_answer_key_matches_its_OpenAPI_document() => AssertMatches<DemoShop.SimStore>("shop");
}
