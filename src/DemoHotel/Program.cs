using System.Security.Cryptography;
using System.Text;
using DemoHotel;

// Demo Hotel: a second benchmark target in another domain (bookings). Its seeded defects are business-rule faults a
// generic oracle cannot see (dates, capacity, pricing, permissions, lifecycle), so finding them depends on rules written
// from its user stories. Two roles: guests (anonymous, scoped to their browser) and front-desk staff (signed in).
// The test harness (defect and feature switches, story edits, resets) needs the harness key and is left out of OpenAPI.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<Hotel>();
builder.Services.AddOpenApi(o => o.AddDocumentTransformer((doc, _, _) =>
{
    doc.Info.Title = "Demo Hotel API";
    doc.Info.Description = "Search rooms, book and cancel stays; front-desk check-in and check-out.";
    return Task.CompletedTask;
}));
var app = builder.Build();

var harnessKey = builder.Configuration["AdminKey"] is { Length: > 0 } k ? k : Environment.GetEnvironmentVariable("DEMO_SHOP_KEY") is { Length: > 0 } e ? e
    : throw new InvalidOperationException("Set DEMO_SHOP_KEY: node scripts/dev.mjs creates a random one in .env (see README, Setup).");
bool Harness(HttpContext h)
{
    var sent = h.Request.Headers.Authorization.ToString().Replace("Bearer ", "", StringComparison.OrdinalIgnoreCase).Trim();
    return sent.Length > 0 && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(sent), Encoding.UTF8.GetBytes(harnessKey));
}
static string? Guest(HttpContext h) => h.Request.Headers[Hotel.GuestHeader].FirstOrDefault() is { Length: >= 8 and <= 64 } g ? g : null;
static string? StaffToken(HttpContext h) => h.Request.Headers[Hotel.StaffHeader].FirstOrDefault();
IResult Problem(string? error, int status) => Results.Problem(error, statusCode: status);

app.MapGet("/", (HttpContext h) =>
{
    h.Response.Headers.ContentSecurityPolicy = "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
    h.Response.Headers.CacheControl = "no-store";
    return Results.Content(Page.Html, "text/html; charset=utf-8");
}).ExcludeFromDescription();
app.MapOpenApi();

var api = app.MapGroup("/api");

// ---- guests ----
api.MapGet("/rooms", (Hotel hotel) => Results.Ok(new RoomList(hotel.Rooms())))
    .WithSummary("Rooms with their nightly rate and capacity").Produces<RoomList>();
api.MapPost("/bookings", (BookingDto d, HttpContext h, Hotel hotel) =>
{
    if (Guest(h) is not { } guest) return Problem($"Send the {Hotel.GuestHeader} header.", 400);
    var (booking, error, status) = hotel.Book(d, guest);
    return error is null ? Results.Json(booking, statusCode: 201) : Problem(error, status);
}).WithSummary("Book a stay").Produces<Booking>(201).ProducesProblem(400).ProducesProblem(404);
api.MapGet("/bookings", (HttpContext h, Hotel hotel) => Results.Ok(new BookingList(hotel.BookingsOf(Guest(h)))))
    .WithSummary("The caller's bookings").Produces<BookingList>();
api.MapPost("/bookings/{id}/cancel", (string id, HttpContext h, Hotel hotel) =>
{
    var (booking, error, status) = hotel.Cancel(id, Guest(h));
    return error is null ? Results.Ok(booking) : Problem(error, status);
}).WithSummary("Cancel one of the caller's bookings").Produces<Booking>().ProducesProblem(404).ProducesProblem(409);

// ---- front desk ----
api.MapPost("/staff/login", (StaffLoginDto d, Hotel hotel) =>
    hotel.Login(d) is { } token ? Results.Ok(new StaffSession(token, Hotel.StaffName)) : Problem("Invalid email or password.", 401))
    .WithSummary("Front-desk sign-in").Produces<StaffSession>().ProducesProblem(401);
api.MapPost("/staff/logout", (HttpContext h, Hotel hotel) => { hotel.Logout(StaffToken(h)); return Results.NoContent(); }).WithSummary("Front-desk sign-out");
api.MapGet("/staff/me", (HttpContext h, Hotel hotel) => hotel.IsStaff(StaffToken(h)) ? Results.Ok(new { name = Hotel.StaffName }) : Problem("Not signed in.", 401))
    .WithSummary("The signed-in front-desk user");
api.MapGet("/staff/bookings", (HttpContext h, Hotel hotel) =>
    hotel.StaffAllowed(StaffToken(h)) ? Results.Ok(new BookingList(hotel.AllBookings())) : Problem("Front-desk sign-in required.", 401))
    .WithSummary("All bookings, for the front desk").Produces<BookingList>().ProducesProblem(401);
api.MapPost("/staff/bookings/{id}/checkin", (string id, HttpContext h, Hotel hotel) =>
{
    if (!hotel.StaffAllowed(StaffToken(h))) return Problem("Front-desk sign-in required.", 401);
    var (booking, error, status) = hotel.CheckIn(id);
    return error is null ? Results.Ok(booking) : Problem(error, status);
}).WithSummary("Check a guest in").Produces<Booking>().ProducesProblem(401).ProducesProblem(409);
api.MapPost("/staff/bookings/{id}/checkout", (string id, HttpContext h, Hotel hotel) =>
{
    if (!hotel.StaffAllowed(StaffToken(h))) return Problem("Front-desk sign-in required.", 401);
    var (booking, error, status) = hotel.CheckOut(id);
    return error is null ? Results.Ok(booking) : Problem(error, status);
}).WithSummary("Check a guest out").Produces<Booking>().ProducesProblem(401).ProducesProblem(409);

// ---- test harness (harness key; not part of the product API) ----
var harness = api.MapGroup("").ExcludeFromDescription();
harness.MapGet("/stories", (Hotel hotel) => Results.Ok(hotel.StoryList()));
harness.MapGet("/admin/defects", (HttpContext h, Hotel hotel) => Harness(h) ? Results.Ok(hotel.Switches(Hotel.Defects, hotel.On)) : Results.StatusCode(401));
harness.MapPut("/admin/defects", (Dictionary<string, bool> flags, HttpContext h, Hotel hotel) =>
    !Harness(h) ? Results.StatusCode(401) : hotel.Set(Hotel.Defects, hotel.On, flags) is { } err ? Problem(err, 400) : Results.Ok(hotel.Switches(Hotel.Defects, hotel.On)));
harness.MapGet("/test/features", (HttpContext h, Hotel hotel) => Harness(h) ? Results.Ok(hotel.Switches(Hotel.Features, hotel.Enabled)) : Results.StatusCode(401));
harness.MapPut("/test/features", (Dictionary<string, bool> flags, HttpContext h, Hotel hotel) =>
    !Harness(h) ? Results.StatusCode(401) : hotel.Set(Hotel.Features, hotel.Enabled, flags) is { } err ? Problem(err, 400) : Results.Ok(hotel.Switches(Hotel.Features, hotel.Enabled)));
harness.MapPut("/test/stories/{key}", (string key, StoryEditDto d, HttpContext h, Hotel hotel) =>
    !Harness(h) ? Results.StatusCode(401) : hotel.EditStory(key, d) is { } s ? Results.Ok(s) : Results.NotFound());
// Test data back to the seed; defect and feature switches and stories are kept (they are set by the benchmark).
harness.MapPost("/test/reset", (HttpContext h, Hotel hotel) => { if (!Harness(h)) return Results.StatusCode(401); hotel.ResetData(); return Results.Ok(new { removed = true }); });
// Everything back to the seed, for the start of a scenario: data, stories, defects and features.
harness.MapPost("/test/reset-all", (HttpContext h, Hotel hotel) => { if (!Harness(h)) return Results.StatusCode(401); hotel.ResetAll(); return Results.Ok(new { reset = true }); });

app.Run();
