using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BugHunter.Api.Common;

namespace BugHunter.Api.Hunter;

public record DemoDefect(string Id, string Title, string Description, bool Enabled, string? Requires = null); // Requires: the feature it lives in
public record DemoStory(string Key, string Title, string Criteria);

// Talks to the demo apps with seeded defects: the shop (src/DemoShop) and the hotel (src/DemoHotel). They share one
// admin contract: defect switches for benchmark scoring, user stories, a keyed test-reset hook, and (shop only) the
// catalog for checking search results. Nothing here is used when hunting any other site.
public class DemoShopClient(IHttpClientFactory http, HunterOptions opts)
{
    public const string Shop = "shop", Hotel = "hotel";
    public static readonly Dictionary<string, string> Names = new() { [Shop] = "Demo shop", [Hotel] = "Demo Hotel" };

    public string? UrlOf(string app) => (app == Hotel ? opts.DemoHotelUrl : opts.DemoShopUrl) is { } u && !string.IsNullOrWhiteSpace(u) ? u.TrimEnd('/') + "/" : null;
    public string? Url => UrlOf(Shop);
    public string? ResetUrlOf(string app) => UrlOf(app) is { } u ? u + "api/test/reset" : null;
    public string? ResetUrl => ResetUrlOf(Shop);
    public string? AdminKey => opts.DemoShopKey;
    public IEnumerable<string> Configured => Names.Keys.Where(a => UrlOf(a) is not null);

    // Which demo app a hunt target is, by host and port; null for any other site.
    public string? AppFor(string target) =>
        Uri.TryCreate(target, UriKind.Absolute, out var t)
            ? Configured.FirstOrDefault(a => Uri.TryCreate(UrlOf(a), UriKind.Absolute, out var d) && t.Authority == d.Authority)
            : null;
    public bool IsDemo(string target) => AppFor(target) is not null;

    HttpRequestMessage Req(string app, HttpMethod method, string path, object? body = null)
    {
        var r = new HttpRequestMessage(method, (UrlOf(app) ?? throw new HttpRequestException($"The {Names[app]} URL is not configured.")) + path);
        if (AdminKey is { Length: > 0 } k) r.Headers.Authorization = new("Bearer", k);
        if (body is not null) r.Content = JsonContent.Create(body);
        return r;
    }

    async Task<JsonNode?> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var client = http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        using var res = await client.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
    }

    public async Task<List<DemoDefect>> DefectsAsync(CancellationToken ct, string app = Shop) =>
        ((await SendAsync(Req(app, HttpMethod.Get, "api/admin/defects"), ct)) as JsonArray ?? [])
            .OfType<JsonObject>().Select(d => new DemoDefect(J.Text(d["id"]), J.Text(d["title"]), J.Text(d["description"]), d["enabled"]?.GetValue<bool>() == true,
                d["requires"] is JsonValue r ? r.ToString() : null)).ToList();

    public async Task<List<DemoDefect>> SetDefectsAsync(Dictionary<string, bool> flags, CancellationToken ct, string app = Shop)
    {
        await SendAsync(Req(app, HttpMethod.Put, "api/admin/defects", flags), ct);
        return await DefectsAsync(ct, app);
    }

    public async Task<List<(string Name, string Description)>> CatalogAsync(CancellationToken ct) =>
        ((await SendAsync(Req(Shop, HttpMethod.Get, "api/products"), ct))?["items"] as JsonArray ?? [])
            .OfType<JsonObject>().Select(p => (J.Text(p["name"]), J.Text(p["description"]))).ToList();

    public async Task<List<DemoStory>> StoriesAsync(CancellationToken ct, string app = Shop) =>
        ((await SendAsync(Req(app, HttpMethod.Get, "api/stories"), ct)) as JsonArray ?? [])
            .OfType<JsonObject>().Select(s => new DemoStory(J.Text(s["key"]), J.Text(s["title"]), J.Text(s["criteria"]))).ToList();
}
