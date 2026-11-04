using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

namespace DemoShop;

public record ShopProduct(string Id, string Name, decimal Price, string Category, string Emoji, double Rating, string Description, int Stock);

public static class SimShopCatalog
{
    public static readonly ShopProduct[] All =
    [
        new("p-100", "Trail Running Shoes", 89.99m, "Footwear", "👟", 4.6, "Lightweight shoes with a grippy sole for mixed terrain.", 25),
        new("p-101", "Leather Office Sneakers", 119.00m, "Footwear", "👞", 4.3, "Minimal leather sneakers that pass for dress shoes.", 12),
        new("p-102", "Insulated Water Bottle", 24.50m, "Outdoor", "🍶", 4.8, "Keeps drinks cold for 24 hours. 750 ml.", 80),
        new("p-103", "Ultralight Daypack", 59.90m, "Outdoor", "🎒", 4.5, "22 L pack with a hydration sleeve.", 18),
        new("p-104", "Wireless Earbuds", 79.00m, "Electronics", "🎧", 4.2, "Noise cancelling with 30 hours of battery.", 40),
        new("p-105", "Smart Watch", 199.00m, "Electronics", "⌚", 4.4, "Heart-rate, GPS and sleep tracking.", 9),
        new("p-106", "USB-C Fast Charger", 19.99m, "Electronics", "🔌", 4.7, "65 W GaN charger with two ports.", 150),
        new("p-107", "Merino Wool Socks (3 pack)", 29.00m, "Apparel", "🧦", 4.9, "Temperature regulating, no-blister seams.", 60),
        new("p-108", "Rain Jacket", 139.00m, "Apparel", "🧥", 4.1, "Waterproof, breathable and packable.", 5),
        new("p-109", "Yoga Mat", 34.00m, "Fitness", "🧘", 4.5, "6 mm non-slip mat with carry strap.", 45),
        new("p-110", "Adjustable Dumbbell Set", 249.00m, "Fitness", "🏋️", 4.7, "5-25 kg adjustable pair.", 3),
        new("p-111", "Coffee Grinder", 44.99m, "Home", "☕", 4.0, "Burr grinder with 15 settings.", 0),
    ];
}

public class SimShopService(SimStore st)
{
    const string S = "app";
    public const string SessionHeader = "X-Sim-Session";
    public const string StaffEmail = "staff@shop.demo", StaffPassword = "Staff123!";
    public const decimal GiftWrapPrice = 4.99m;
    public static readonly Dictionary<string, (string Title, string Description)> Defects = new()
    {
        ["coupon-stacking"] = ("Coupons stack", "Applying the same coupon repeatedly keeps increasing the discount."),
        ["expired-card"] = ("Expired cards accepted", "Checkout does not validate the card expiry date."),
        ["negative-qty"] = ("Zero / negative quantity", "Cart accepts quantities of zero or less, producing negative totals."),
        ["search-case"] = ("Case-sensitive search", "Searching for 'shoes' does not find 'Shoes'."),
        ["no-stock-check"] = ("Overselling", "Checkout succeeds even when the quantity exceeds the available stock."),
        ["tax-rounding"] = ("Tax rounded down", "Sales tax is truncated instead of rounded, so totals are a cent short."),
        ["staff-open"] = ("Order desk open to shoppers", "The staff order list and status changes work for any signed-in shopper."),
        ["cancel-shipped"] = ("Cancel after shipping", "A shopper can cancel an order that has already shipped or been delivered."),
        ["listing-price-stale"] = ("Listed price differs from the cart", "The product list shows the Rain Jacket at $119.00 while the cart charges $139.00."),
        ["gift-wrap-per-item"] = ("Gift wrap charged per item", "Gift wrap is charged for every item instead of once per order (needs the gift-wrap feature)."),
    };

    // Defects that live inside a feature and can only show when it is on.
    public static readonly Dictionary<string, string> Requires = new() { ["gift-wrap-per-item"] = "gift-wrap" };

    // Changes to the app's surface that benchmark scenarios switch on between runs.
    public static readonly Dictionary<string, (string Title, string Description)> Features = new()
    {
        ["gift-wrap"] = ("Gift wrap", "Checkout offers gift wrap for $4.99 per order."),
    };
    static readonly Dictionary<string, (string Kind, decimal Value, string Label)> Coupons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SAVE10"] = ("percent", 10, "10% off"), ["WELCOME5"] = ("amount", 5, "$5 off"), ["FREESHIP"] = ("shipping", 0, "Free shipping"),
    };

    static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    static string Hash(string email, string pw) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{email.ToLowerInvariant()}|{pw}|simshop"))).ToLowerInvariant();

    public async Task PrepareAsync(CancellationToken ct)
    {
        await SeedAsync(ct);
        if (await st.GetAsync(S, "user", DemoEmail, ct) is null)
            await st.PutAsync(S, "user", DemoEmail, new JsonObject { ["name"] = "Shopper 01", ["hash"] = Hash(DemoEmail, "Shop123!"), ["createdAt"] = SimStore.Now() }, ct);
        if (await st.GetAsync(S, "user", StaffEmail, ct) is null)
            await st.PutAsync(S, "user", StaffEmail, new JsonObject { ["name"] = "Store staff", ["role"] = "staff", ["hash"] = Hash(StaffEmail, StaffPassword), ["createdAt"] = SimStore.Now() }, ct);
    }

    public const string DemoEmail = "shopper01@shop.demo";

    // Back to the seeded shop: orders, carts, sessions, stock and accounts made since are removed. keepDefects keeps the
    // defect and feature switches and the edited stories (set by the benchmark); without it everything is reseeded.
    public async Task<int> ResetAsync(CancellationToken ct, bool keepDefects = false)
    {
        var kept = new List<(string Kind, JsonObject Doc)>();
        if (keepDefects)
            foreach (var kind in new[] { "defects", "features" })
                if (await st.GetAsync(S, kind, "flags", ct) is { } flags) kept.Add((kind, flags));
        var stories = keepDefects ? await st.ListAsync(S, "story", ct) : [];
        var removed = await st.ClearAsync(S, ct);
        await PrepareAsync(ct);
        foreach (var (kind, doc) in kept) await st.PutAsync(S, kind, "flags", doc, ct);
        foreach (var s in stories) await st.PutAsync(S, "story", s["_key"]!.GetValue<string>(), s, ct);
        return removed;
    }

    Task SeedAsync(CancellationToken ct) => st.EnsureSeededAsync(S, async () =>
    {
        await st.PutAsync(S, "user", "shopper01@shop.demo", new JsonObject { ["name"] = "Shopper 01", ["hash"] = Hash("shopper01@shop.demo", "Shop123!"), ["createdAt"] = SimStore.Now() }, ct, false);
        foreach (var p in SimShopCatalog.All) await st.PutAsync(S, "stock", p.Id, new JsonObject { ["qty"] = p.Stock }, ct, false);
        await st.PutAsync(S, "defects", "flags", new JsonObject(), ct, false);
        await st.PutAsync(S, "features", "flags", new JsonObject(), ct, false);
        foreach (var s in Stories.All) await st.PutAsync(S, "story", s.Key, StoryDoc(s.Title, s.Criteria, 1, DateTime.UnixEpoch), ct, false);
        await st.PutAsync(S, "order", "ORD-1001", new JsonObject
        {
            ["id"] = "ORD-1001", ["email"] = "shopper01@shop.demo", ["status"] = "Delivered", ["createdAt"] = DateTime.UtcNow.AddDays(-12).ToString("o"),
            ["shippedAt"] = DateTime.UtcNow.AddDays(-11).ToString("o"), ["deliveredAt"] = DateTime.UtcNow.AddDays(-9).ToString("o"),
            ["lines"] = new JsonArray(new JsonObject { ["productId"] = "p-102", ["name"] = "Insulated Water Bottle", ["price"] = 24.5, ["qty"] = 1 }),
            ["subtotal"] = 24.5, ["discount"] = 0, ["shipping"] = 5.99, ["tax"] = 1.96, ["total"] = 32.45, ["shipTo"] = "12 Market St, Springfield 12345", ["cardLast4"] = "4242"
        }, ct, false);
    }, ct);

    async Task<HashSet<string>> OnAsync(string kind, CancellationToken ct) =>
        (await st.GetAsync(S, kind, "flags", ct) ?? new JsonObject()).Where(kv => kv.Value is JsonValue v && v.TryGetValue<bool>(out var b) && b).Select(kv => kv.Key).ToHashSet();
    public Task<HashSet<string>> ActiveDefectsAsync(CancellationToken ct) => OnAsync("defects", ct);
    public Task<HashSet<string>> ActiveFeaturesAsync(CancellationToken ct) => OnAsync("features", ct);

    async Task<object> SwitchesAsync(string kind, Dictionary<string, (string Title, string Description)> all, CancellationToken ct)
    {
        await PrepareAsync(ct); var on = await OnAsync(kind, ct);
        return all.Select(d => new { id = d.Key, title = d.Value.Title, description = d.Value.Description, enabled = on.Contains(d.Key), requires = Requires.GetValueOrDefault(d.Key) });
    }

    async Task<string?> SetSwitchesAsync(string kind, Dictionary<string, (string Title, string Description)> all, Dictionary<string, bool> flags, CancellationToken ct)
    {
        await PrepareAsync(ct);
        if (flags.Keys.FirstOrDefault(k => !all.ContainsKey(k)) is { } bad) return $"Unknown switch '{bad}'.";
        var o = await st.GetAsync(S, kind, "flags", ct) ?? new JsonObject();
        foreach (var kv in flags) o[kv.Key] = kv.Value;
        await st.PutAsync(S, kind, "flags", o, ct); return null;
    }

    public Task<object> DefectsAsync(CancellationToken ct) => SwitchesAsync("defects", Defects, ct);
    public Task<string?> SetDefectsAsync(Dictionary<string, bool> flags, CancellationToken ct) => SetSwitchesAsync("defects", Defects, flags, ct);
    public Task<object> FeaturesAsync(CancellationToken ct) => SwitchesAsync("features", Features, ct);
    public Task<string?> SetFeaturesAsync(Dictionary<string, bool> flags, CancellationToken ct) => SetSwitchesAsync("features", Features, flags, ct);

    // ---- stories (versioned: benchmark scenarios edit them between runs) ----
    static JsonObject StoryDoc(string title, string criteria, int version, DateTime updatedAt) =>
        new() { ["title"] = title, ["criteria"] = criteria, ["version"] = version, ["updatedAt"] = updatedAt.ToString("o") };
    static object StoryView(JsonObject s) => new { key = Str(s["_key"]), title = Str(s["title"]), criteria = Str(s["criteria"]), version = s["version"]!.GetValue<int>(), updatedAt = Str(s["updatedAt"]) };

    public async Task<IEnumerable<object>> StoriesAsync(CancellationToken ct)
    {
        await PrepareAsync(ct);
        return (await st.ListAsync(S, "story", ct)).OrderBy(s => Str(s["_key"])).Select(StoryView);
    }

    public async Task<object?> EditStoryAsync(string key, string? title, string? criteria, CancellationToken ct)
    {
        await PrepareAsync(ct); key = key.ToUpperInvariant();
        if (await st.GetAsync(S, "story", key, ct) is not { } s) return null;
        var next = StoryDoc(title?.Trim() is { Length: > 0 } t ? t : Str(s["title"])!, criteria?.Trim() is { Length: > 0 } c ? c : Str(s["criteria"])!, s["version"]!.GetValue<int>() + 1, DateTime.UtcNow);
        await st.PutAsync(S, "story", key, next, ct);
        next["_key"] = key; return StoryView(next);
    }

    public async Task<object> CheckoutOptionsAsync(CancellationToken ct)
    {
        await PrepareAsync(ct);
        return (await ActiveFeaturesAsync(ct)).Contains("gift-wrap") ? new { giftWrapPrice = GiftWrapPrice } : new { };
    }

    public async Task<object> ProductsAsync(string? q, string? category, CancellationToken ct)
    {
        await PrepareAsync(ct); var on = await ActiveDefectsAsync(ct);
        var stock = (await st.ListAsync(S, "stock", ct)).ToDictionary(s => s["_key"]!.GetValue<string>(), s => s["qty"]!.GetValue<int>());
        var cmp = on.Contains("search-case") ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var items = SimShopCatalog.All.Where(p => (string.IsNullOrEmpty(category) || p.Category == category) && (string.IsNullOrWhiteSpace(q) || p.Name.Contains(q.Trim(), cmp) || p.Description.Contains(q.Trim(), cmp)))
            .Select(p => new { p.Id, p.Name, Price = p.Id == "p-108" && on.Contains("listing-price-stale") ? 119.00m : p.Price, p.Category, p.Emoji, p.Rating, p.Description, stock = stock.GetValueOrDefault(p.Id, p.Stock) });
        return new { items, categories = SimShopCatalog.All.Select(p => p.Category).Distinct().Order() };
    }

    // ---- accounts ----
    public async Task<(object? Result, string? Error, int Status)> RegisterAsync(string name, string email, string password, CancellationToken ct)
    {
        await PrepareAsync(ct); email = (email ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80) return (null, "Name is required.", 400);
        if (!Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")) return (null, "Enter a valid email address.", 400);
        if ((password ?? "").Length < 8) return (null, "Password must be at least 8 characters.", 400);
        if (await st.GetAsync(S, "user", email, ct) is not null) return (null, "An account with this email already exists.", 409);
        await st.PutAsync(S, "user", email, new JsonObject { ["name"] = name.Trim(), ["hash"] = Hash(email, password!), ["createdAt"] = SimStore.Now() }, ct);
        return await LoginAsync(email, password!, ct);
    }

    public async Task<(object? Result, string? Error, int Status)> LoginAsync(string email, string password, CancellationToken ct)
    {
        await PrepareAsync(ct); email = (email ?? "").Trim().ToLowerInvariant();
        var u = await st.GetAsync(S, "user", email, ct);
        if (u is null || Str(u["hash"]) != Hash(email, password ?? "")) return (null, "Invalid email or password.", 400);
        var token = Guid.NewGuid().ToString("N");
        await st.PutAsync(S, "session", token, new JsonObject { ["email"] = email, ["at"] = SimStore.Now() }, ct);
        return (new { token, email, name = Str(u["name"]) }, null, 200);
    }

    public async Task<string?> RoleAsync(string email, CancellationToken ct) => Str((await st.GetAsync(S, "user", email, ct))?["role"]);

    public async Task<(string? Email, string? Name)> WhoAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token)) return (null, null);
        var s = await st.GetAsync(S, "session", token, ct); if (s is null) return (null, null);
        var u = await st.GetAsync(S, "user", Str(s["email"])!, ct); return (Str(s["email"]), Str(u?["name"]));
    }

    public Task LogoutAsync(string? token, CancellationToken ct) => string.IsNullOrEmpty(token) ? Task.CompletedTask : st.DeleteAsync(S, "session", token, ct);

    // ---- cart ----
    async Task<JsonObject> CartAsync(string email, CancellationToken ct) => await st.GetAsync(S, "cart", email, ct) ?? new JsonObject { ["items"] = new JsonArray(), ["coupons"] = new JsonArray() };

    public async Task<object> CartViewAsync(string email, CancellationToken ct)
    {
        await PrepareAsync(ct); var cart = await CartAsync(email, ct); var on = await ActiveDefectsAsync(ct);
        return Totals(cart, on);
    }

    static object Totals(JsonObject cart, HashSet<string> on)
    {
        var lines = cart["items"]!.AsArray().Select(i =>
        {
            var p = SimShopCatalog.All.First(x => x.Id == Str(i!["productId"])); var qty = i!["qty"]!.GetValue<int>();
            return new { productId = p.Id, name = p.Name, emoji = p.Emoji, price = p.Price, qty, lineTotal = Math.Round(p.Price * qty, 2) };
        }).ToList();
        var subtotal = lines.Sum(l => l.lineTotal); decimal discount = 0; var freeShip = false; var labels = new List<string>();
        foreach (var c in cart["coupons"]!.AsArray().Select(Str))
        {
            if (c is null || !Coupons.TryGetValue(c, out var def)) continue; labels.Add($"{c.ToUpperInvariant()} ({def.Label})");
            discount += def.Kind switch { "percent" => subtotal * def.Value / 100m, "amount" => def.Value, _ => 0m };
            if (def.Kind == "shipping") freeShip = true;
        }
        discount = Math.Round(Math.Min(on.Contains("coupon-stacking") ? decimal.MaxValue : subtotal, discount), 2);
        var taxable = subtotal - discount; var shipping = lines.Count == 0 || freeShip || subtotal >= 50 ? 0m : 5.99m;
        var tax = on.Contains("tax-rounding") ? Math.Floor(taxable * 0.08m * 100m) / 100m : Math.Round(taxable * 0.08m, 2, MidpointRounding.AwayFromZero);
        return new { items = lines, coupons = labels, subtotal, discount, shipping, tax, total = Math.Round(taxable + shipping + tax, 2), count = lines.Sum(l => l.qty) };
    }

    public async Task<(object? Result, string? Error, int Status)> SetQtyAsync(string email, string productId, int qty, bool add, CancellationToken ct)
    {
        await PrepareAsync(ct); var on = await ActiveDefectsAsync(ct);
        var p = SimShopCatalog.All.FirstOrDefault(x => x.Id == productId); if (p is null) return (null, "Product not found.", 404);
        var cart = await CartAsync(email, ct); var items = cart["items"]!.AsArray(); var line = items.FirstOrDefault(i => Str(i!["productId"]) == productId)?.AsObject();
        var target = add ? (line?["qty"]?.GetValue<int>() ?? 0) + qty : qty;
        if (!on.Contains("negative-qty") && qty < (add ? 1 : 0)) return (null, "Quantity must be at least 1.", 400);
        if (target > 99) return (null, "You can order at most 99 of an item.", 400);
        if (target <= 0 && !on.Contains("negative-qty")) { if (line is not null) items.Remove(line); }
        else if (line is null) items.Add(new JsonObject { ["productId"] = productId, ["qty"] = target });
        else line["qty"] = target;
        await st.PutAsync(S, "cart", email, cart, ct); return (Totals(cart, on), null, 200);
    }

    public async Task<object> RemoveAsync(string email, string productId, CancellationToken ct)
    {
        await PrepareAsync(ct); var cart = await CartAsync(email, ct); var items = cart["items"]!.AsArray();
        if (items.FirstOrDefault(i => Str(i!["productId"]) == productId) is { } l) items.Remove(l);
        await st.PutAsync(S, "cart", email, cart, ct); return Totals(cart, await ActiveDefectsAsync(ct));
    }

    public async Task<(object? Result, string? Error, int Status)> CouponAsync(string email, string code, CancellationToken ct)
    {
        await PrepareAsync(ct); var on = await ActiveDefectsAsync(ct); code = (code ?? "").Trim().ToUpperInvariant();
        if (!Coupons.ContainsKey(code)) return (null, $"Coupon '{code}' is not valid.", 400);
        var cart = await CartAsync(email, ct); var cs = cart["coupons"]!.AsArray();
        if (cart["items"]!.AsArray().Count == 0) return (null, "Add items to your cart before applying a coupon.", 400);
        if (!on.Contains("coupon-stacking") && cs.Any(c => Str(c) == code)) return (null, "This coupon is already applied.", 409);
        if (!on.Contains("coupon-stacking")) cs.Clear();
        cs.Add(code); await st.PutAsync(S, "cart", email, cart, ct); return (Totals(cart, on), null, 200);
    }

    public async Task<object> ClearCouponsAsync(string email, CancellationToken ct)
    {
        await PrepareAsync(ct); var cart = await CartAsync(email, ct); cart["coupons"]!.AsArray().Clear(); await st.PutAsync(S, "cart", email, cart, ct);
        return Totals(cart, await ActiveDefectsAsync(ct));
    }

    // ---- checkout ----
    public record CheckoutDto(string? Name, string? Address, string? City, string? Zip, string? CardNumber, string? Expiry, string? Cvv, bool? GiftWrap = null);

    static bool Luhn(string n) { var sum = 0; var alt = false; for (var i = n.Length - 1; i >= 0; i--) { var d = n[i] - '0'; if (alt) { d *= 2; if (d > 9) d -= 9; } sum += d; alt = !alt; } return sum % 10 == 0; }

    public async Task<(object? Result, Dictionary<string, string>? Errors, string? Error, int Status)> CheckoutAsync(string email, CheckoutDto d, CancellationToken ct)
    {
        await PrepareAsync(ct); var on = await ActiveDefectsAsync(ct); var cart = await CartAsync(email, ct);
        if (cart["items"]!.AsArray().Count == 0) return (null, null, "Your cart is empty.", 400);
        var errs = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(d.Name)) errs["name"] = "Full name is required.";
        if (string.IsNullOrWhiteSpace(d.Address)) errs["address"] = "Street address is required.";
        if (string.IsNullOrWhiteSpace(d.City)) errs["city"] = "City is required.";
        if (!Regex.IsMatch(d.Zip ?? "", @"^\d{5}$")) errs["zip"] = "ZIP code must be 5 digits.";
        var card = Regex.Replace(d.CardNumber ?? "", @"[\s-]", "");
        if (!Regex.IsMatch(card, @"^\d{13,19}$") || !Luhn(card)) errs["cardNumber"] = "Card number is invalid.";
        var m = Regex.Match(d.Expiry ?? "", @"^(0[1-9]|1[0-2])\/(\d{2})$");
        if (!m.Success) errs["expiry"] = "Use the format MM/YY.";
        else if (!on.Contains("expired-card"))
        {
            var exp = new DateTime(2000 + int.Parse(m.Groups[2].Value), int.Parse(m.Groups[1].Value), 1).AddMonths(1);
            if (exp <= DateTime.UtcNow) errs["expiry"] = "Your card has expired.";
        }
        if (!Regex.IsMatch(d.Cvv ?? "", @"^\d{3,4}$")) errs["cvv"] = "CVV must be 3 or 4 digits.";
        if (errs.Count > 0) return (null, errs, "Please correct the highlighted fields.", 422);
        if (card == "4000000000000002") return (null, null, "Your card was declined by the issuer.", 402);

        var stock = (await st.ListAsync(S, "stock", ct)).ToDictionary(s => s["_key"]!.GetValue<string>(), s => s);
        if (!on.Contains("no-stock-check"))
            foreach (var i in cart["items"]!.AsArray())
            {
                var pid = Str(i!["productId"])!; var have = stock[pid]["qty"]!.GetValue<int>();
                if (i["qty"]!.GetValue<int>() > have) return (null, null, $"Only {have} of '{SimShopCatalog.All.First(p => p.Id == pid).Name}' left in stock.", 409);
            }
        var view = Totals(cart, on); var t = SimStore.Obj(view);
        var giftWrap = (await ActiveFeaturesAsync(ct)).Contains("gift-wrap") && d.GiftWrap == true;
        var giftWrapFee = giftWrap ? (on.Contains("gift-wrap-per-item") ? t["count"]!.GetValue<int>() : 1) * GiftWrapPrice : 0m;
        var n = await st.NextAsync(S, "order", 1001, ct); var id = $"ORD-{n}";
        foreach (var i in cart["items"]!.AsArray()) { var pid = Str(i!["productId"])!; stock[pid]["qty"] = Math.Max(0, stock[pid]["qty"]!.GetValue<int>() - i["qty"]!.GetValue<int>()); await st.PutAsync(S, "stock", pid, stock[pid], ct, false); }
        var order = new JsonObject
        {
            ["id"] = id, ["email"] = email, ["status"] = "Processing", ["createdAt"] = SimStore.Now(), ["lines"] = t["items"]!.DeepClone(), ["subtotal"] = t["subtotal"]!.DeepClone(), ["discount"] = t["discount"]!.DeepClone(),
            ["shipping"] = t["shipping"]!.DeepClone(), ["tax"] = t["tax"]!.DeepClone(), ["total"] = t["total"]!.GetValue<decimal>() + giftWrapFee, ["shipTo"] = $"{d.Address!.Trim()}, {d.City!.Trim()} {d.Zip}", ["cardLast4"] = card[^4..]
        };
        if (giftWrap) { order["giftWrap"] = true; order["giftWrapFee"] = giftWrapFee; }
        await st.PutAsync(S, "order", id, order, ct, false);
        cart["items"]!.AsArray().Clear(); cart["coupons"]!.AsArray().Clear(); await st.PutAsync(S, "cart", email, cart, ct);
        return (order, null, null, 201);
    }

    public async Task<List<JsonObject>> OrdersAsync(string email, CancellationToken ct) { await PrepareAsync(ct); return (await st.ListAsync(S, "order", ct)).Where(o => Str(o["email"]) == email).OrderByDescending(o => Str(o["createdAt"])).ToList(); }
    public async Task<JsonObject?> OrderAsync(string email, string id, CancellationToken ct) { await PrepareAsync(ct); var o = await st.GetAsync(S, "order", id.ToUpperInvariant(), ct); return o is not null && Str(o["email"]) == email ? o : null; }

    // Order lifecycle: Processing -> Shipped -> Delivered (staff), or Processing -> Cancelled (the shopper; stock returns).
    public async Task<(object? Result, string? Error, int Status)> CancelOrderAsync(string email, string id, CancellationToken ct)
    {
        await PrepareAsync(ct); var on = await ActiveDefectsAsync(ct);
        if (await OrderAsync(email, id, ct) is not { } o) return (null, "Order not found.", 404);
        var status = Str(o["status"]);
        if (status != "Processing" && !(on.Contains("cancel-shipped") && status is "Shipped" or "Delivered")) return (null, $"An order that is {status} cannot be cancelled.", 409);
        foreach (var l in o["lines"]!.AsArray())
            if (await st.GetAsync(S, "stock", Str(l!["productId"])!, ct) is { } s) { s["qty"] = s["qty"]!.GetValue<int>() + l["qty"]!.GetValue<int>(); await st.PutAsync(S, "stock", Str(l["productId"])!, s, ct, false); }
        o["status"] = "Cancelled"; o["cancelledAt"] = SimStore.Now();
        await st.PutAsync(S, "order", Str(o["_key"])!, o, ct);
        return (o, null, 200);
    }

    public async Task<bool> StaffAllowedAsync(string email, CancellationToken ct) => await RoleAsync(email, ct) == "staff" || (await ActiveDefectsAsync(ct)).Contains("staff-open");

    public async Task<List<JsonObject>> AllOrdersAsync(CancellationToken ct) { await PrepareAsync(ct); return (await st.ListAsync(S, "order", ct)).OrderByDescending(o => Str(o["createdAt"])).ToList(); }

    public async Task<(object? Result, string? Error, int Status)> SetOrderStatusAsync(string id, string? next, CancellationToken ct)
    {
        await PrepareAsync(ct);
        if (await st.GetAsync(S, "order", id.ToUpperInvariant(), ct) is not { } o) return (null, "Order not found.", 404);
        var status = Str(o["status"]);
        var allowed = (status, next) is ("Processing", "Shipped") or ("Shipped", "Delivered");
        if (!allowed) return (null, $"An order that is {status} cannot become {next}.", 409);
        o["status"] = next; o[next == "Shipped" ? "shippedAt" : "deliveredAt"] = SimStore.Now();
        await st.PutAsync(S, "order", Str(o["_key"])!, o, ct);
        return (o, null, 200);
    }
}

public record AuthDto(string? Name, string? Email, string? Password);
public record QtyDto(string ProductId, int Qty);
public record QtyOnlyDto(int Qty);
public record CouponDto(string Code);
public record StatusDto(string? Status);
public record StoryEditDto(string? Title, string? Criteria);

public static class SimShopEndpoints
{
    static IResult R((object? Result, string? Error, int Status) r) => r.Error is not null ? SimStore.Fail(r.Error, r.Status) : Results.Json(r.Result, statusCode: r.Status);

    // Shopper-facing routes of the storefront.
    public static void MapShopRoutes(RouteGroupBuilder g)
    {
        static string? Tok(HttpContext h) => h.Request.Headers[SimShopService.SessionHeader].FirstOrDefault();
        async Task<(string? Email, IResult? Deny)> Auth(HttpContext h, SimShopService s, CancellationToken ct)
        {
            await s.PrepareAsync(ct); var (email, _) = await s.WhoAsync(Tok(h), ct);
            return email is null ? (null, SimStore.Fail("Please sign in to continue.", 403)) : (email, null);
        }

        g.MapGet("/products", async (SimShopService s, string? q, string? category, CancellationToken ct) => Results.Ok(await s.ProductsAsync(q, category, ct)));
        g.MapPost("/register", async (AuthDto d, SimShopService s, CancellationToken ct) => R(await s.RegisterAsync(d.Name ?? "", d.Email ?? "", d.Password ?? "", ct)));
        g.MapPost("/login", async (AuthDto d, SimShopService s, CancellationToken ct) => R(await s.LoginAsync(d.Email ?? "", d.Password ?? "", ct)));
        g.MapPost("/logout", async (HttpContext h, SimShopService s, CancellationToken ct) => { await s.LogoutAsync(Tok(h), ct); return Results.NoContent(); });
        g.MapGet("/me", async (HttpContext h, SimShopService s, CancellationToken ct) => { await s.PrepareAsync(ct); var (e, n) = await s.WhoAsync(Tok(h), ct); return e is null ? SimStore.Fail("Not signed in.", 403) : Results.Ok(new { email = e, name = n, role = await s.RoleAsync(e, ct) ?? "shopper" }); });
        g.MapGet("/cart", async (HttpContext h, SimShopService s, CancellationToken ct) => { var (e, d) = await Auth(h, s, ct); return d ?? Results.Ok(await s.CartViewAsync(e!, ct)); });
        g.MapPost("/cart/items", async (QtyDto b, HttpContext h, SimShopService s, CancellationToken ct) => { var (e, d) = await Auth(h, s, ct); return d ?? R(await s.SetQtyAsync(e!, b.ProductId, b.Qty, true, ct)); });
        g.MapPut("/cart/items/{id}", async (string id, QtyOnlyDto b, HttpContext h, SimShopService s, CancellationToken ct) => { var (e, d) = await Auth(h, s, ct); return d ?? R(await s.SetQtyAsync(e!, id, b.Qty, false, ct)); });
        g.MapDelete("/cart/items/{id}", async (string id, HttpContext h, SimShopService s, CancellationToken ct) => { var (e, d) = await Auth(h, s, ct); return d ?? Results.Ok(await s.RemoveAsync(e!, id, ct)); });
        g.MapPost("/cart/coupon", async (CouponDto b, HttpContext h, SimShopService s, CancellationToken ct) => { var (e, d) = await Auth(h, s, ct); return d ?? R(await s.CouponAsync(e!, b.Code, ct)); });
        g.MapDelete("/cart/coupon", async (HttpContext h, SimShopService s, CancellationToken ct) => { var (e, d) = await Auth(h, s, ct); return d ?? Results.Ok(await s.ClearCouponsAsync(e!, ct)); });
        g.MapPost("/checkout", async (SimShopService.CheckoutDto b, HttpContext h, SimShopService s, CancellationToken ct) =>
        {
            var (e, d) = await Auth(h, s, ct); if (d is not null) return d;
            var (res, errs, err, status) = await s.CheckoutAsync(e!, b, ct);
            return err is null ? Results.Json(res, statusCode: status) : Results.Json(new { title = err, status, fieldErrors = errs }, statusCode: status);
        });
        g.MapGet("/orders", async (HttpContext h, SimShopService s, CancellationToken ct) => { var (e, d) = await Auth(h, s, ct); return d ?? Results.Ok(await s.OrdersAsync(e!, ct)); });
        g.MapGet("/orders/{id}", async (string id, HttpContext h, SimShopService s, CancellationToken ct) => { var (e, d) = await Auth(h, s, ct); return d ?? (await s.OrderAsync(e!, id, ct) is { } o ? (IResult)Results.Ok(o) : Results.NotFound()); });
        g.MapPost("/orders/{id}/cancel", async (string id, HttpContext h, SimShopService s, CancellationToken ct) => { var (e, d) = await Auth(h, s, ct); return d ?? R(await s.CancelOrderAsync(e!, id, ct)); });
        g.MapGet("/checkout/options", async (SimShopService s, CancellationToken ct) => Results.Ok(await s.CheckoutOptionsAsync(ct)));

        // Staff: the order desk.
        async Task<IResult?> Staff(HttpContext h, SimShopService s, CancellationToken ct)
        {
            var (e, d) = await Auth(h, s, ct);
            return d ?? (await s.StaffAllowedAsync(e!, ct) ? null : SimStore.Fail("Staff only.", 403));
        }
        g.MapGet("/staff/orders", async (HttpContext h, SimShopService s, CancellationToken ct) => await Staff(h, s, ct) ?? Results.Ok(await s.AllOrdersAsync(ct)));
        g.MapPost("/staff/orders/{id}/status", async (string id, StatusDto b, HttpContext h, SimShopService s, CancellationToken ct) => await Staff(h, s, ct) ?? R(await s.SetOrderStatusAsync(id, b.Status, ct)));
    }
}
