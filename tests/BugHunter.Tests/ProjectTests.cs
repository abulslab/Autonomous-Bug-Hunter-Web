using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using BugHunter.Api.Projects;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BugHunter.Tests;

static class Paths
{
    public static string Root([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
    public static string GroundTruth => Path.Combine(Root(), "ground-truth");
}

// The simulators stand in for Jira and Confluence until phase R, so their contracts are tested like the real ones.
public class SimulatorTests(WebApplicationFactory<Simulators.SimData> f) : IClassFixture<WebApplicationFactory<Simulators.SimData>>
{
    HttpClient Api()
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("qa@example.test:" + TestKeys.Sim)));
        return c;
    }

    async Task Reset()
    {
        var h = f.CreateClient(); h.DefaultRequestHeaders.Authorization = new("Bearer", TestKeys.Demo);
        Assert.Equal(HttpStatusCode.OK, (await h.PostAsync("/sim/reset", null)).StatusCode);
    }

    [Fact]
    public async Task Jira_serves_the_backlog_and_records_every_change()
    {
        await Reset();
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().GetAsync("/rest/api/3/search?jql=project=HOTEL")).StatusCode);
        var c = Api();
        var page = JsonNode.Parse(await c.GetStringAsync("/rest/api/3/search?jql=" + Uri.EscapeDataString("project = HOTEL AND issuetype = Story")))!;
        Assert.Equal(6, page["total"]!.GetValue<int>());
        Assert.Equal("doc", page["issues"]![0]!["fields"]!["description"]!["type"]!.GetValue<string>()); // ADF, as Jira Cloud v3

        var edit = new { fields = new { description = Adf.Doc("The total is nights times the rate.\nBreakfast is extra.") } };
        Assert.Equal(HttpStatusCode.NoContent, (await c.PutAsJsonAsync("/rest/api/3/issue/HOTEL-3", edit)).StatusCode);
        var log = JsonNode.Parse(await c.GetStringAsync("/rest/api/3/issue/HOTEL-3/changelog"))!;
        Assert.Equal("description", log["values"]![0]!["items"]![0]!["field"]!.GetValue<string>());

        var bug = await c.PostAsJsonAsync("/rest/api/3/issue", new { fields = new { project = new { key = "HOTEL" }, summary = "Rate differs", issuetype = new { name = "Bug" }, description = Adf.Doc("Seen on B-1001.") } });
        Assert.Equal("HOTEL-7", JsonNode.Parse(await bug.Content.ReadAsStringAsync())!["key"]!.GetValue<string>());
    }

    [Fact]
    public async Task Confluence_versions_pages_with_optimistic_locking()
    {
        await Reset();
        var c = Api();
        var space = JsonNode.Parse(await c.GetStringAsync("/wiki/api/v2/spaces?keys=HOTEL"))!["results"]![0]!;
        var pages = JsonNode.Parse(await c.GetStringAsync($"/wiki/api/v2/spaces/{space["id"]}/pages?body-format=storage"))!["results"]!.AsArray();
        Assert.Equal(3, pages.Count);
        var p = pages.First(x => x!["title"]!.GetValue<string>() == "Booking rules")!;
        Assert.Contains("<li>", p["body"]!["storage"]!["value"]!.GetValue<string>());

        var stale = await c.PutAsJsonAsync($"/wiki/api/v2/pages/{p["id"]}", new { title = "Booking rules", body = new { representation = "storage", value = "<p>x</p>" }, version = new { number = 1 } });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var ok = await c.PutAsJsonAsync($"/wiki/api/v2/pages/{p["id"]}", new { title = "Booking rules", body = new { representation = "storage", value = "<p>Breakfast is $15.</p>" }, version = new { number = 2 } });
        Assert.Equal(2, JsonNode.Parse(await ok.Content.ReadAsStringAsync())!["version"]!["number"]!.GetValue<int>());
    }

    // The adapters Bug Hunter uses for real Jira and Confluence, run against the simulators.
    [Fact]
    public async Task The_Jira_and_Confluence_adapters_read_the_simulators()
    {
        await Reset();
        var stories = await new JiraStorySource(f.CreateClient(), "http://localhost", "DEMO", "qa@example.test", TestKeys.Sim).StoriesAsync(default);
        Assert.Equal(8, stories.Count);
        Assert.Contains("rounded to the nearest cent", stories.Single(s => s.Key == "DEMO-121").Criteria);
        var docs = await new ConfluenceDocSource(f.CreateClient(), "http://localhost", "DEMO", "qa@example.test", TestKeys.Sim).DocumentsAsync(default);
        Assert.Contains(docs, d => d.Title == "Pricing and promotions" && d.Text.Contains("- Each coupon applies once per cart"));
        await Assert.ThrowsAsync<KnowledgeSourceException>(() => new JiraStorySource(f.CreateClient(), "http://localhost", "DEMO", "qa@example.test", "wrong").StoriesAsync(default));
    }
}

public class KnowledgeTests
{
    static List<SourceStory> Stories(string criteria) => [new("HOTEL-3", "Correct price", criteria, null, null), new("HOTEL-1", "Valid stay dates", "Check-in is today or later.", null, null)];

    [Fact]
    public void A_changed_story_gets_a_new_version_and_a_criteria_level_diff()
    {
        var v1 = KnowledgeDiffer.Next("hotel", null, Stories("The total is nights times the rate."), [], "jira", "none");
        Assert.All(v1.Stories, s => Assert.Equal(1, s.Version));
        Assert.True(v1.Diff.Empty); // a first snapshot has nothing to compare with

        var same = KnowledgeDiffer.Next("hotel", v1, Stories("The total is nights times the rate.  "), [], "jira", "none");
        Assert.True(same.Diff.Empty); // whitespace is not a change

        var v2 = KnowledgeDiffer.Next("hotel", v1, Stories("The total is nights times the rate.\nBreakfast is charged per guest per night."), [], "jira", "none");
        var change = Assert.Single(v2.Diff.StoriesChanged);
        Assert.Equal(("HOTEL-3", 1, 2), (change.Key, change.From, change.To));
        Assert.Equal(["Breakfast is charged per guest per night."], change.CriteriaAdded);
        Assert.Empty(change.CriteriaRemoved);
        Assert.Equal(1, v2.Stories.Single(s => s.Key == "HOTEL-1").Version);
    }

    // The knowledge files seed the Jira simulator; the demo apps serve their own copy for the benchmark harness. Both
    // must say the same, or a hunt grounded in Jira would test against different criteria than the app was built to.
    static async Task AssertSame<T>(string app) where T : class
    {
        await using var factory = new WebApplicationFactory<T>();
        var c = factory.CreateClient(); c.DefaultRequestHeaders.Authorization = new("Bearer", TestKeys.Demo);
        await c.PostAsync("/api/test/reset-all", null);
        var served = JsonNode.Parse(await c.GetStringAsync("/api/stories"))!.AsArray().Select(s => (s!["key"]!.ToString(), s["title"]!.ToString(), s["criteria"]!.ToString())).ToList();
        var file = JsonNode.Parse(File.ReadAllText(Path.Combine(Paths.GroundTruth, app, "knowledge", "stories.json")))!["stories"]!.AsArray()
            .Select(s => (s!["key"]!.ToString(), s["title"]!.ToString(), s["criteria"]!.ToString())).ToList();
        Assert.Equal(file, served);
    }

    [Fact] public Task Hotel_knowledge_matches_the_stories_the_app_serves() => AssertSame<DemoHotel.Hotel>("hotel");
    [Fact] public Task Shop_knowledge_matches_the_stories_the_app_serves() => AssertSame<DemoShop.SimStore>("shop");
}

public class ProjectApiTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    static object Hotel(string id) => new
    {
        id, name = "Hotel from files", mission = "Find bugs in bookings.",
        environments = new[] { new { name = "local", url = "http://localhost:5291/", resetUrl = "http://localhost:5291/api/test/reset", resetSecret = "reset-key" } },
        roles = new object[] { new { id = "guest", name = "Guest", secrets = new Dictionary<string, string>() }, new { id = "front-desk", name = "Front desk", secrets = new Dictionary<string, string> { ["email"] = "desk-email", ["password"] = "desk-password" } } },
        knowledge = new
        {
            stories = new { kind = "files", path = Path.Combine(Paths.GroundTruth, "hotel", "knowledge", "stories.json") },
            documents = new { kind = "files", path = Path.Combine(Paths.GroundTruth, "hotel", "knowledge", "pages") }
        },
        skillPacks = new[] { "bookings" }
    };

    [Fact]
    public async Task A_project_keeps_secrets_write_only_and_versions_its_knowledge()
    {
        var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync("/api/projects", Hotel("hotel-files"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/api/projects", Hotel("hotel-files"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/projects", Hotel("Bad Id"))).StatusCode);
        var traversal = new
        {
            id = "bad-path", name = "Traversal", environments = new[] { new { name = "local", url = "http://localhost:5291/" } },
            knowledge = new { stories = new { kind = "files", path = Path.Combine(Paths.GroundTruth, "..", "BugHunter.sln") } }
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/projects", traversal)).StatusCode);

        await c.PutAsJsonAsync("/api/projects/hotel-files/secrets", new Dictionary<string, string> { ["desk-email"] = "desk@hotel.demo", ["desk-password"] = "Desk123!" });
        var view = await c.GetStringAsync("/api/projects/hotel-files");
        Assert.Contains("desk-password", view);
        Assert.DoesNotContain("Desk123!", view); // names only, never values
        Assert.DoesNotContain("\"_key\"", view); // the store's own bookkeeping is not a secret

        var first = JsonNode.Parse(await (await c.PostAsync("/api/projects/hotel-files/sync", null)).Content.ReadAsStringAsync())!;
        Assert.Equal((1, true, 6, 3), (first["version"]!.GetValue<int>(), first["changed"]!.GetValue<bool>(), first["stories"]!.GetValue<int>(), first["documents"]!.GetValue<int>()));
        var again = JsonNode.Parse(await (await c.PostAsync("/api/projects/hotel-files/sync", null)).Content.ReadAsStringAsync())!;
        Assert.Equal((1, false), (again["version"]!.GetValue<int>(), again["changed"]!.GetValue<bool>()));
        var k = JsonNode.Parse(await c.GetStringAsync("/api/projects/hotel-files/knowledge"))!;
        Assert.Equal("HOTEL-1", k["stories"]![0]!["key"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_project_hunt_uses_the_role_account_and_fails_clearly_without_it()
    {
        var c = f.CreateClient();
        await c.PostAsJsonAsync("/api/projects", Hotel("hotel-hunt"));
        var missing = await c.PostAsJsonAsync("/api/projects/hotel-hunt/hunts", new { role = "front-desk", strategy = "Heuristic", connection = "heuristic" });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("desk-email", await missing.Content.ReadAsStringAsync());

        await c.PutAsJsonAsync("/api/projects/hotel-hunt/secrets", new Dictionary<string, string> { ["desk-email"] = "desk@hotel.demo", ["desk-password"] = "Desk123!", ["reset-key"] = "k" });
        // Valid now: it gets as far as the browser worker, which this test host does not run.
        var ok = await c.PostAsJsonAsync("/api/projects/hotel-hunt/hunts", new { role = "front-desk", strategy = "Heuristic", connection = "heuristic" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ok.StatusCode);
    }

    [Fact]
    public async Task The_demo_apps_exist_as_projects_on_a_fresh_install()
    {
        var list = JsonNode.Parse(await f.CreateClient().GetStringAsync("/api/projects"))!.AsArray();
        var shop = list.Single(p => p!["id"]!.GetValue<string>() == "shop")!;
        Assert.Equal(["shopper", "staff"], shop["roles"]!.AsArray().Select(r => r!["id"]!.GetValue<string>()));
        Assert.Equal("jira", shop["knowledge"]!["stories"]!["kind"]!.GetValue<string>()); // the simulators are configured
    }
}
