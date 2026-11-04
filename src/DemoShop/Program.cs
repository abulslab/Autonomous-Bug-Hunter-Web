using System.Security.Cryptography;
using System.Text;
using DemoShop;

// Demo Supply Co.: a small shop with switchable seeded defects, used as Bug Hunter's benchmark target.
// Shopper pages and API are public; defect switches and the test-reset hook need the admin key.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<SimStore>();
builder.Services.AddScoped<SimShopService>();
builder.Services.AddOpenApi(o => o.AddDocumentTransformer((doc, _, _) =>
{
    doc.Info.Title = "Demo Supply Co. API";
    doc.Info.Description = "Browse and search products, manage a cart with coupons, check out, and manage orders.";
    return Task.CompletedTask;
}));
var app = builder.Build();

var adminKey = builder.Configuration["AdminKey"] is { Length: > 0 } k ? k : Environment.GetEnvironmentVariable("DEMO_SHOP_KEY") is { Length: > 0 } e ? e
    : throw new InvalidOperationException("Set DEMO_SHOP_KEY: node scripts/dev.mjs creates a random one in .env (see README, Setup).");
bool Admin(HttpContext h)
{
    var sent = h.Request.Headers.Authorization.ToString().Replace("Bearer ", "", StringComparison.OrdinalIgnoreCase).Trim();
    return sent.Length > 0 && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(sent), Encoding.UTF8.GetBytes(adminKey));
}

app.MapGet("/", (HttpContext h) =>
{
    h.Response.Headers.ContentSecurityPolicy = Storefront.Csp;
    h.Response.Headers.CacheControl = "no-store";
    return Results.Content(Storefront.Html, "text/html; charset=utf-8");
}).ExcludeFromDescription();
app.MapOpenApi();

var api = app.MapGroup("/api");
SimShopEndpoints.MapShopRoutes(api);

// Test harness (harness key; not part of the product API, so left out of OpenAPI).
var harness = api.MapGroup("").ExcludeFromDescription();
// The shop's backlog, versioned: acceptance criteria Bug Hunter can turn into rules.
harness.MapGet("/stories", async (SimShopService s, CancellationToken ct) => Results.Ok(await s.StoriesAsync(ct)));
harness.MapPut("/test/stories/{key}", async (string key, StoryEditDto d, HttpContext h, SimShopService s, CancellationToken ct) =>
    !Admin(h) ? Results.StatusCode(401) : await s.EditStoryAsync(key, d.Title, d.Criteria, ct) is { } story ? Results.Ok(story) : Results.NotFound());
harness.MapGet("/admin/defects", async (HttpContext h, SimShopService s, CancellationToken ct) => Admin(h) ? Results.Ok(await s.DefectsAsync(ct)) : Results.StatusCode(401));
harness.MapPut("/admin/defects", async (Dictionary<string, bool> flags, HttpContext h, SimShopService s, CancellationToken ct) =>
    !Admin(h) ? Results.StatusCode(401) : await s.SetDefectsAsync(flags, ct) is { } err ? SimStore.Fail(err) : Results.Ok(await s.DefectsAsync(ct)));
harness.MapGet("/test/features", async (HttpContext h, SimShopService s, CancellationToken ct) => Admin(h) ? Results.Ok(await s.FeaturesAsync(ct)) : Results.StatusCode(401));
harness.MapPut("/test/features", async (Dictionary<string, bool> flags, HttpContext h, SimShopService s, CancellationToken ct) =>
    !Admin(h) ? Results.StatusCode(401) : await s.SetFeaturesAsync(flags, ct) is { } err ? SimStore.Fail(err) : Results.Ok(await s.FeaturesAsync(ct)));
// Test data back to the seed, keeping switches and stories; reset-all also reseeds those (the start of a scenario).
harness.MapPost("/test/reset", async (HttpContext h, SimShopService s, CancellationToken ct) =>
    Admin(h) ? Results.Ok(new { removed = await s.ResetAsync(ct, keepDefects: true) }) : Results.StatusCode(401));
harness.MapPost("/test/reset-all", async (HttpContext h, SimShopService s, CancellationToken ct) =>
    Admin(h) ? Results.Ok(new { removed = await s.ResetAsync(ct) }) : Results.StatusCode(401));

app.Run();
