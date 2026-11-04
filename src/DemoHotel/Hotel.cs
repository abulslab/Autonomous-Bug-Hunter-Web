using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace DemoHotel;

public record Room(string Id, string Name, decimal Rate, int MaxGuests, string Description);
public record RoomView(string Id, string Name, decimal Rate, int MaxGuests, string Description,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? BreakfastRate);
public record RoomList(IEnumerable<RoomView> Items);
public record BookingDto(string? RoomId, string? CheckIn, string? CheckOut, int Guests, string? Name, string? Email, bool? Breakfast);
public record BookingList(IEnumerable<Booking> Items);
public record StaffLoginDto(string? Email, string? Password);
public record StaffSession(string Token, string Name);
public record Story(string Key, string Title, string Criteria, int Version, DateTime UpdatedAt);
public record StoryEditDto(string? Title, string? Criteria);

// Status: Confirmed -> CheckedIn -> CheckedOut, or Confirmed -> Cancelled.
public record Booking(string Id, string RoomId, string RoomName, string CheckIn, string CheckOut, int Nights, decimal Rate, int Guests, int MaxGuests,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Breakfast,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? BreakfastTotal,
    decimal Total, string Name, string Email, string Status, DateTime CreatedAt,
    DateTime? CheckedInAt = null, DateTime? CheckedOutAt = null, DateTime? CancelledAt = null)
{
    [JsonIgnore] public string GuestId { get; init; } = "";
}

public class Hotel
{
    public const string GuestHeader = "X-Guest", StaffHeader = "X-Staff-Session";
    public const string StaffEmail = "desk@hotel.demo", StaffPassword = "Desk123!", StaffName = "Front desk";
    public const decimal BreakfastRate = 15m;

    public static readonly Room[] All =
    [
        new("r-101", "Harbour Single", 89m, 1, "A quiet single room facing the harbour."),
        new("r-102", "Harbour Double", 129m, 2, "Double bed, harbour view."),
        new("r-201", "Family Suite", 219m, 4, "Two rooms, sleeps four."),
        new("r-301", "Penthouse", 459m, 2, "Top floor, private terrace."),
    ];

    public static readonly Dictionary<string, (string Title, string Description)> Defects = new()
    {
        ["past-checkin"] = ("Past check-in accepted", "A booking can start on a date that has already passed."),
        ["checkout-before-checkin"] = ("Check-out before check-in accepted", "A booking can end before or on the day it starts."),
        ["over-capacity"] = ("Room capacity ignored", "More guests than the room sleeps, or none, can be booked."),
        ["wrong-total"] = ("Last night not charged", "Stays of three nights or more are charged one night less."),
        ["desk-open"] = ("Front desk open to anyone", "The front-desk booking list and check-in work without a front-desk sign-in."),
        ["cancel-after-checkin"] = ("Cancel after check-in", "A guest can cancel a booking that is already checked in or checked out."),
        ["quote-mismatch"] = ("Charged rate differs from the quote", "The Penthouse is booked at $499 a night while the room list shows $459."),
        ["breakfast-once"] = ("Breakfast charged once", "Breakfast is charged once per guest for the whole stay instead of every night (needs the breakfast feature)."),
    };

    // Defects that live inside a feature and can only show when it is on.
    public static readonly Dictionary<string, string> Requires = new() { ["breakfast-once"] = "breakfast" };

    // Changes to the app's surface that benchmark scenarios switch on between runs.
    public static readonly Dictionary<string, (string Title, string Description)> Features = new()
    {
        ["breakfast"] = ("Breakfast add-on", "Rooms offer breakfast at $15 per guest per night; bookings take a breakfast option."),
    };

    static readonly Story[] SeedStories =
    [
        new("HOTEL-1", "Valid stay dates", "Check-in is today or later.\nCheck-out is after check-in; a stay is at least one night.", 1, DateTime.UnixEpoch),
        new("HOTEL-2", "Room capacity", "The number of guests is between 1 and the room's maximum.", 1, DateTime.UnixEpoch),
        new("HOTEL-3", "Correct price", "The total is the number of nights times the nightly rate.", 1, DateTime.UnixEpoch),
        new("HOTEL-4", "Guests can cancel before check-in", "A guest can cancel their own booking while it is confirmed.\nA booking that is checked in or checked out cannot be cancelled.\nA cancelled booking shows as Cancelled.", 1, DateTime.UnixEpoch),
        new("HOTEL-5", "Front desk", "Only signed-in front-desk staff can see all bookings and check guests in or out.\nA guest is checked in before being checked out.", 1, DateTime.UnixEpoch),
        new("HOTEL-6", "The quoted rate is the rate charged", "The nightly rate on a booking is the rate shown for that room in the room list.", 1, DateTime.UnixEpoch),
    ];

    public readonly HashSet<string> On = [], Enabled = [];
    readonly ConcurrentDictionary<string, Booking> _bookings = new();
    readonly ConcurrentDictionary<string, bool> _sessions = new();
    readonly ConcurrentDictionary<string, Story> _stories = new();
    int _next;

    public Hotel() => ResetAll();

    public void ResetData()
    {
        _bookings.Clear(); _sessions.Clear(); _next = 1000;
        // Another guest's booking, so the front desk has something to show and a leak of other guests' data is visible.
        var day = DateOnly.FromDateTime(DateTime.UtcNow);
        var seed = new Booking("B-1000", "r-201", "Family Suite", day.AddDays(14).ToString("yyyy-MM-dd"), day.AddDays(16).ToString("yyyy-MM-dd"), 2, 219m, 3, 4,
            null, null, 438m, "Ada Lovelace", "ada@example.test", "Confirmed", DateTime.UtcNow.AddDays(-3)) { GuestId = "seed-guest-0001" };
        _bookings[seed.Id] = seed;
    }

    public void ResetAll()
    {
        On.Clear(); Enabled.Clear(); _stories.Clear();
        foreach (var s in SeedStories) _stories[s.Key] = s;
        ResetData();
    }

    public object Switches(Dictionary<string, (string Title, string Description)> all, HashSet<string> on) =>
        all.Select(d => new { id = d.Key, title = d.Value.Title, description = d.Value.Description, enabled = on.Contains(d.Key), requires = Requires.GetValueOrDefault(d.Key) });

    public string? Set(Dictionary<string, (string Title, string Description)> all, HashSet<string> on, Dictionary<string, bool> flags)
    {
        if (flags.Keys.FirstOrDefault(k => !all.ContainsKey(k)) is { } bad) return $"Unknown switch '{bad}'.";
        foreach (var (key, value) in flags) if (value) on.Add(key); else on.Remove(key);
        return null;
    }

    public IEnumerable<Story> StoryList() => _stories.Values.OrderBy(s => s.Key);

    public Story? EditStory(string key, StoryEditDto d)
    {
        if (!_stories.TryGetValue(key.ToUpperInvariant(), out var s)) return null;
        var next = s with { Title = d.Title?.Trim() is { Length: > 0 } t ? t : s.Title, Criteria = d.Criteria?.Trim() is { Length: > 0 } c ? c : s.Criteria, Version = s.Version + 1, UpdatedAt = DateTime.UtcNow };
        _stories[s.Key] = next;
        return next;
    }

    bool Breakfast => Enabled.Contains("breakfast");
    decimal RateOf(Room r) => r.Id == "r-301" && On.Contains("quote-mismatch") ? 499m : r.Rate;

    public IEnumerable<RoomView> Rooms() => All.Select(r => new RoomView(r.Id, r.Name, r.Rate, r.MaxGuests, r.Description, Breakfast ? BreakfastRate : null));

    static DateOnly? Date(string? s) => DateOnly.TryParseExact(s ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    public (Booking? Booking, string? Error, int Status) Book(BookingDto d, string guest)
    {
        var room = All.FirstOrDefault(r => r.Id == d.RoomId);
        if (room is null) return (null, "Room not found.", 404);
        if (string.IsNullOrWhiteSpace(d.Name) || string.IsNullOrWhiteSpace(d.Email) || !d.Email.Contains('@')) return (null, "Enter your name and a valid email.", 400);
        if (Date(d.CheckIn) is not { } inDate || Date(d.CheckOut) is not { } outDate) return (null, "Dates must be yyyy-MM-dd.", 400);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (inDate < today && !On.Contains("past-checkin")) return (null, "Check-in cannot be in the past.", 400);
        if (outDate <= inDate && !On.Contains("checkout-before-checkin")) return (null, "Check-out must be after check-in.", 400);
        if ((d.Guests < 1 || d.Guests > room.MaxGuests) && !On.Contains("over-capacity")) return (null, $"This room sleeps 1 to {room.MaxGuests} guests.", 400);
        var nights = outDate.DayNumber - inDate.DayNumber;
        var charged = On.Contains("wrong-total") && nights >= 3 ? nights - 1 : nights;
        var rate = RateOf(room);
        bool? breakfast = Breakfast ? d.Breakfast == true : null;
        decimal? breakfastTotal = breakfast is true ? (On.Contains("breakfast-once") ? 1 : Math.Max(0, nights)) * Math.Max(0, d.Guests) * BreakfastRate : breakfast is false ? 0m : null;
        var b = new Booking($"B-{Interlocked.Increment(ref _next)}", room.Id, room.Name, d.CheckIn!, d.CheckOut!, nights, rate, d.Guests, room.MaxGuests,
            breakfast, breakfastTotal, Math.Max(0, charged) * rate + (breakfastTotal ?? 0), d.Name!.Trim(), d.Email!.Trim(), "Confirmed", DateTime.UtcNow) { GuestId = guest };
        _bookings[b.Id] = b;
        return (b, null, 201);
    }

    public IEnumerable<Booking> BookingsOf(string? guest) =>
        guest is null ? [] : _bookings.Values.Where(b => b.GuestId == guest).OrderByDescending(b => b.CreatedAt);

    public IEnumerable<Booking> AllBookings() => _bookings.Values.OrderByDescending(b => b.CreatedAt);

    public (Booking? Booking, string? Error, int Status) Cancel(string id, string? guest)
    {
        if (!_bookings.TryGetValue(id, out var b) || guest is null || b.GuestId != guest) return (null, "Booking not found.", 404);
        var allowed = b.Status == "Confirmed" || On.Contains("cancel-after-checkin") && b.Status is "CheckedIn" or "CheckedOut";
        if (!allowed) return (null, $"A booking that is {b.Status} cannot be cancelled.", 409);
        return (_bookings[id] = b with { Status = "Cancelled", CancelledAt = DateTime.UtcNow }, null, 200);
    }

    public string? Login(StaffLoginDto d)
    {
        if (!string.Equals(d.Email?.Trim(), StaffEmail, StringComparison.OrdinalIgnoreCase) || d.Password != StaffPassword) return null;
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _sessions[token] = true;
        return token;
    }

    public void Logout(string? token) { if (token is not null) _sessions.TryRemove(token, out _); }
    public bool IsStaff(string? token) => token is not null && _sessions.ContainsKey(token);
    public bool StaffAllowed(string? token) => IsStaff(token) || On.Contains("desk-open");

    public (Booking? Booking, string? Error, int Status) CheckIn(string id)
    {
        if (!_bookings.TryGetValue(id, out var b)) return (null, "Booking not found.", 404);
        if (b.Status != "Confirmed") return (null, $"A booking that is {b.Status} cannot be checked in.", 409);
        return (_bookings[id] = b with { Status = "CheckedIn", CheckedInAt = DateTime.UtcNow }, null, 200);
    }

    public (Booking? Booking, string? Error, int Status) CheckOut(string id)
    {
        if (!_bookings.TryGetValue(id, out var b)) return (null, "Booking not found.", 404);
        if (b.Status != "CheckedIn") return (null, $"A booking that is {b.Status} cannot be checked out.", 409);
        return (_bookings[id] = b with { Status = "CheckedOut", CheckedOutAt = DateTime.UtcNow }, null, 200);
    }
}
