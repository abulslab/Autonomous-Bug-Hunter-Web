using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Simulators;

// Jira and Confluence simulators for building and testing Bug Hunter without the real services. Each exposes the subset
// of the real REST API that Bug Hunter uses, with the same paths and JSON shapes (Jira Cloud v3, Confluence Cloud v2),
// so an adapter written against a simulator works against the real service with another base URL and credentials.
// API calls need the API token (Basic email:token, as Atlassian Cloud expects, or Bearer token). /sim/* is the test
// harness (reset), behind the harness key.
var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;
var dir = cfg["Simulators:GroundTruthDir"] is { Length: > 0 } d ? d : SimData.FindGroundTruth(builder.Environment.ContentRootPath);
builder.Services.AddSingleton(new SimData(dir));
var app = builder.Build();

var apiToken = cfg["Simulators:ApiToken"] is { Length: > 0 } t ? t : Environment.GetEnvironmentVariable("SIM_TOKEN") is { Length: > 0 } et ? et
    : throw new InvalidOperationException("Set SIM_TOKEN: node scripts/dev.mjs creates a random one in .env (see README, Setup).");
var harnessKey = cfg["Simulators:HarnessKey"] is { Length: > 0 } hk ? hk : Environment.GetEnvironmentVariable("DEMO_SHOP_KEY") is { Length: > 0 } eh ? eh
    : throw new InvalidOperationException("Set DEMO_SHOP_KEY: node scripts/dev.mjs creates a random one in .env (see README, Setup).");
static bool Same(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
bool Api(HttpContext h)
{
    var auth = h.Request.Headers.Authorization.ToString();
    if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return Same(auth[7..].Trim(), apiToken);
    if (!auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return false;
    try { var pair = Encoding.UTF8.GetString(Convert.FromBase64String(auth[6..].Trim())); return pair.Contains(':') && Same(pair[(pair.IndexOf(':') + 1)..], apiToken); }
    catch (FormatException) { return false; }
}
app.Use(async (h, next) =>
{
    var p = h.Request.Path.Value ?? "";
    var allowed = p.StartsWith("/sim/") ? h.Request.Headers.Authorization.ToString() is var a && a.StartsWith("Bearer ") && Same(a[7..].Trim(), harnessKey)
        : !(p.StartsWith("/rest/") || p.StartsWith("/wiki/")) || Api(h);
    if (!allowed) { h.Response.StatusCode = 401; await h.Response.WriteAsJsonAsync(new { errorMessages = new[] { "Authentication required." } }); return; }
    await next();
});

string Base(HttpContext h) => $"{h.Request.Scheme}://{h.Request.Host}";
app.MapGet("/", () => Results.Text("Bug Hunter simulators: Jira (/rest/api/3) and Confluence (/wiki/api/v2)."));

// ---------------- Jira Cloud REST v3 (subset) ----------------
JsonObject IssueJson(Issue i, HttpContext h) => new()
{
    ["id"] = i.Id, ["key"] = i.Key, ["self"] = $"{Base(h)}/rest/api/3/issue/{i.Id}",
    ["fields"] = new JsonObject
    {
        ["summary"] = i.Summary, ["description"] = SimData.Adf(i.Description),
        ["issuetype"] = new JsonObject { ["name"] = i.Type }, ["status"] = new JsonObject { ["name"] = i.Status },
        ["project"] = new JsonObject { ["key"] = i.Project }, ["labels"] = new JsonArray(i.Labels.Select(l => (JsonNode)l).ToArray()),
        ["created"] = i.Created.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"), ["updated"] = i.Updated.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"),
        ["issuelinks"] = new JsonArray(h.RequestServices.GetRequiredService<SimData>().Links.Where(l => l.Inward == i.Key || l.Outward == i.Key).Select(l => (JsonNode)new JsonObject
        {
            ["id"] = l.Id, ["type"] = new JsonObject { ["name"] = l.Type },
            [l.Inward == i.Key ? "outwardIssue" : "inwardIssue"] = new JsonObject { ["key"] = l.Inward == i.Key ? l.Outward : l.Inward },
        }).ToArray()),
        ["comment"] = new JsonObject { ["total"] = i.Comments.Count, ["comments"] = new JsonArray(i.Comments.Select(c => (JsonNode)new JsonObject { ["id"] = c.Id, ["body"] = SimData.Adf(c.Body), ["created"] = c.Created.ToString("o") }).ToArray()) }
    }
};

Issue? Find(SimData data, string idOrKey) => data.Issues.TryGetValue(idOrKey, out var i) ? i : data.Issues.Values.FirstOrDefault(x => x.Id == idOrKey);

// JQL subset: project = KEY, issuetype = Name, joined by AND; ORDER BY is ignored (results are by key).
app.MapGet("/rest/api/3/search", (string? jql, int? startAt, int? maxResults, SimData data, HttpContext h) =>
{
    var q = data.Issues.Values.AsEnumerable();
    foreach (Match m in Regex.Matches(jql ?? "", @"(project|issuetype)\s*=\s*""?([\w-]+)""?", RegexOptions.IgnoreCase))
    {
        var (field, value) = (m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value);
        q = field == "project" ? q.Where(i => i.Project.Equals(value, StringComparison.OrdinalIgnoreCase)) : q.Where(i => i.Type.Equals(value, StringComparison.OrdinalIgnoreCase));
    }
    var all = q.OrderBy(i => i.Project).ThenBy(i => int.TryParse(i.Key.Split('-').Last(), out var n) ? n : 0).ToList();
    var (start, max) = (Math.Max(0, startAt ?? 0), Math.Clamp(maxResults ?? 50, 1, 100));
    return Results.Ok(new JsonObject { ["startAt"] = start, ["maxResults"] = max, ["total"] = all.Count, ["issues"] = new JsonArray(all.Skip(start).Take(max).Select(i => (JsonNode)IssueJson(i, h)).ToArray()) });
});
app.MapGet("/rest/api/3/issue/{idOrKey}", (string idOrKey, SimData data, HttpContext h) =>
    Find(data, idOrKey) is { } i ? Results.Ok(IssueJson(i, h)) : Results.NotFound(new { errorMessages = new[] { "Issue does not exist or you do not have permission to see it." } }));
app.MapGet("/rest/api/3/issue/{idOrKey}/changelog", (string idOrKey, SimData data) =>
    Find(data, idOrKey) is not { } i ? Results.NotFound() : Results.Ok(new JsonObject
    {
        ["startAt"] = 0, ["total"] = i.History.Count, ["isLast"] = true,
        ["values"] = new JsonArray(i.History.Select(c => (JsonNode)new JsonObject
        {
            ["id"] = c.Id, ["created"] = c.Created.ToString("o"),
            ["items"] = new JsonArray(new JsonObject { ["field"] = c.Field, ["fromString"] = c.From, ["toString"] = c.To })
        }).ToArray())
    }));
app.MapPost("/rest/api/3/issue", (JsonObject body, SimData data, HttpContext h) =>
{
    var f = body["fields"] as JsonObject;
    var project = f?["project"]?["key"]?.ToString(); var summary = f?["summary"]?.ToString();
    if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(summary)) return Results.BadRequest(new { errors = new { summary = "Summary and project are required." } });
    var labels = (f!["labels"] as JsonArray ?? []).Select(l => l!.ToString()).ToList();
    var i = data.Create(project, f["issuetype"]?["name"]?.ToString() ?? "Bug", summary, SimData.FromAdf(f["description"]), labels);
    return Results.Json(new { id = i.Id, key = i.Key, self = $"{Base(h)}/rest/api/3/issue/{i.Id}" }, statusCode: 201);
});
app.MapPut("/rest/api/3/issue/{idOrKey}", (string idOrKey, JsonObject body, SimData data) =>
{
    if (Find(data, idOrKey) is not { } i) return Results.NotFound();
    var f = body["fields"] as JsonObject;
    data.Edit(i, f?["summary"]?.ToString(), f?["description"] is { } desc ? SimData.FromAdf(desc) : null, (f?["labels"] as JsonArray)?.Select(l => l!.ToString()).ToList());
    return Results.NoContent();
});
// Issue links: {"type":{"name":"Relates"},"inwardIssue":{"key":"HOTEL-12"},"outwardIssue":{"key":"HOTEL-3"}}
app.MapPost("/rest/api/3/issueLink", (JsonObject body, SimData data) =>
{
    var inward = body["inwardIssue"]?["key"]?.ToString() ?? ""; var outward = body["outwardIssue"]?["key"]?.ToString() ?? "";
    if (Find(data, inward) is null || Find(data, outward) is null) return Results.NotFound(new { errorMessages = new[] { "Both issues must exist." } });
    data.Link(body["type"]?["name"]?.ToString() ?? "Relates", Find(data, inward)!.Key, Find(data, outward)!.Key);
    return Results.StatusCode(201);
});
app.MapGet("/rest/api/3/issue/{idOrKey}/transitions", (string idOrKey, SimData data) =>
    Find(data, idOrKey) is null ? Results.NotFound() : Results.Ok(new { transitions = SimData.Transitions.Select(t => new { id = t.Key, name = t.Value, to = new { name = t.Value } }) }));
app.MapPost("/rest/api/3/issue/{idOrKey}/transitions", (string idOrKey, JsonObject body, SimData data) =>
{
    if (Find(data, idOrKey) is not { } i) return Results.NotFound();
    if (!SimData.Transitions.TryGetValue(body["transition"]?["id"]?.ToString() ?? "", out var status)) return Results.BadRequest(new { errorMessages = new[] { "Unknown transition." } });
    data.Transition(i, status);
    return Results.NoContent();
});
app.MapPost("/rest/api/3/issue/{idOrKey}/comment", (string idOrKey, JsonObject body, SimData data) =>
    Find(data, idOrKey) is not { } i ? Results.NotFound() : Results.Json(new { id = data.AddComment(i, SimData.FromAdf(body["body"])).Id }, statusCode: 201));

// ---------------- Confluence Cloud REST v2 (subset) ----------------
JsonObject PageJson(Page p, bool body, PageVersion? v = null)
{
    v ??= p.Current;
    var o = new JsonObject { ["id"] = p.Id, ["status"] = "current", ["title"] = v.Title, ["spaceId"] = p.SpaceId, ["version"] = new JsonObject { ["number"] = v.Number, ["createdAt"] = v.CreatedAt.ToString("o") } };
    if (body) o["body"] = new JsonObject { ["storage"] = new JsonObject { ["representation"] = "storage", ["value"] = v.Storage } };
    return o;
}

app.MapGet("/wiki/api/v2/spaces", (string? keys, SimData data) =>
{
    var wanted = (keys ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var list = data.Spaces.Values.Where(s => wanted.Length == 0 || wanted.Contains(s.Key, StringComparer.OrdinalIgnoreCase));
    return Results.Ok(new { results = list.Select(s => new { id = s.Id, key = s.Key, name = s.Name }) });
});
app.MapPost("/wiki/api/v2/spaces", (JsonObject body, SimData data) =>
{
    var key = body["key"]?.ToString();
    if (string.IsNullOrWhiteSpace(key)) return Results.BadRequest(new { message = "A space needs a key." });
    if (data.Spaces.Values.Any(s => s.Key.Equals(key, StringComparison.OrdinalIgnoreCase))) return Results.Conflict(new { message = $"Space {key} exists." });
    var s = data.CreateSpace(key, body["name"]?.ToString() ?? key);
    return Results.Json(new { id = s.Id, key = s.Key, name = s.Name }, statusCode: 201);
});
app.MapPost("/wiki/api/v2/pages", (JsonObject body, SimData data, HttpContext h) =>
{
    var spaceId = body["spaceId"]?.ToString() ?? ""; var title = body["title"]?.ToString();
    if (!data.Spaces.ContainsKey(spaceId) || string.IsNullOrWhiteSpace(title)) return Results.BadRequest(new { message = "A page needs an existing spaceId and a title." });
    if (data.Pages.Values.Any(p => p.SpaceId == spaceId && p.Current.Title == title)) return Results.BadRequest(new { message = "A page with this title already exists in this space." }); // as Confluence
    return Results.Json(PageJson(data.CreatePage(spaceId, title, body["body"]?["value"]?.ToString() ?? ""), true), statusCode: 200);
});
app.MapGet("/wiki/api/v2/spaces/{id}/pages", (string id, string? bodyFormat, SimData data, HttpContext h) =>
    !data.Spaces.ContainsKey(id) ? Results.NotFound()
    : Results.Ok(new JsonObject { ["results"] = new JsonArray(data.Pages.Values.Where(p => p.SpaceId == id).OrderBy(p => p.Id).Select(p => (JsonNode)PageJson(p, h.Request.Query["body-format"] == "storage")).ToArray()) }));
app.MapGet("/wiki/api/v2/pages/{id}", (string id, int? version, SimData data, HttpContext h) =>
    data.Pages.TryGetValue(id, out var p) ? Results.Ok(PageJson(p, h.Request.Query["body-format"] == "storage", version is { } n ? p.Versions.FirstOrDefault(v => v.Number == n) : null)) : Results.NotFound());
app.MapGet("/wiki/api/v2/pages/{id}/versions", (string id, SimData data) =>
    data.Pages.TryGetValue(id, out var p) ? Results.Ok(new { results = p.Versions.Select(v => new { number = v.Number, createdAt = v.CreatedAt.ToString("o") }).Reverse() }) : Results.NotFound());
app.MapPut("/wiki/api/v2/pages/{id}", (string id, JsonObject body, SimData data) =>
{
    if (!data.Pages.TryGetValue(id, out var p)) return Results.NotFound();
    var next = body["version"]?["number"]?.GetValue<int>() ?? 0;
    if (next != p.Current.Number + 1) return Results.Conflict(new { message = $"Version must be {p.Current.Number + 1}." }); // as Confluence: optimistic locking
    data.EditPage(p, body["title"]?.ToString() ?? p.Current.Title, body["body"]?["value"]?.ToString() ?? p.Current.Storage);
    return Results.Ok(PageJson(p, true));
});

// ---------------- test harness ----------------
app.MapPost("/sim/reset", (SimData data) => { data.Reset(); return Results.Ok(new { issues = data.Issues.Count, pages = data.Pages.Count }); });

app.Run();
