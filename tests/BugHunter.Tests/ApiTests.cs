using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BugHunter.Tests;

// The Bug Hunter API without a browser worker or demo shop: validation and storage only.
public class ApiFactory : WebApplicationFactory<Program>
{
    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "bughunter-tests-" + Guid.NewGuid().ToString("N")[..8]);

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseSetting("Storage:DataDir", DataDir);
        builder.UseSetting("Hunter:AutoStartWorker", "false");
        builder.UseSetting("Hunter:WorkerUrl", "http://127.0.0.1:1");
        builder.UseSetting("Hunter:DemoShopUrl", "http://127.0.0.1:1/");
        builder.UseSetting("Models:OllamaUrl", "http://127.0.0.1:1");
    }
}

public class ApiTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Status_reports_a_missing_worker_and_demo_shop_without_failing()
    {
        var c = f.CreateClient();
        var status = JsonNode.Parse(await c.GetStringAsync("/api/hunter/status"))!;
        Assert.False(status["worker"]!["ok"]!.GetValue<bool>());
        Assert.False(status["storefront"]!["enabled"]!.GetValue<bool>());
        Assert.Empty(JsonNode.Parse(await c.GetStringAsync("/api/hunter/hunts"))!.AsArray());
    }

    [Fact]
    public async Task Hunts_validate_the_target_strategy_secrets_and_rules()
    {
        var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/hunter/hunts", new { url = "ftp://example.test/" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/hunter/hunts", new { url = "http://localhost/", strategy = "Random" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/hunter/hunts", new { url = "http://localhost/", secrets = new Dictionary<string, string> { ["bad name!"] = "x" } })).StatusCode);
        var bad = await c.PostAsJsonAsync("/api/hunter/hunts", new { url = "http://localhost/", rules = "GET /api/cart: total ==" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("does not parse", await bad.Content.ReadAsStringAsync());
        var setup = await c.PostAsJsonAsync("/api/hunter/hunts", new { url = "http://localhost/", setupUrl = "http://evil.test/reset" });
        Assert.Contains("same host", await setup.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Requirement_keys_need_the_demo_shop_but_full_stories_do_not()
    {
        var c = f.CreateClient();
        var keyed = await c.PostAsJsonAsync("/api/hunter/hunts", new { url = "http://localhost/", requirements = new[] { "DEMO-121" } });
        Assert.Equal(HttpStatusCode.BadRequest, keyed.StatusCode);
        Assert.Contains("demo shop", await keyed.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        // With stories given in full the request passes validation and stops only at the missing browser worker.
        var full = await c.PostAsJsonAsync("/api/hunter/hunts", new { url = "http://localhost/", stories = new[] { new { key = "APP-1", title = "Totals", criteria = "The total adds up." } } });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, full.StatusCode);
    }
}

public class DemoShopTests(WebApplicationFactory<DemoShop.SimStore> f) : IClassFixture<WebApplicationFactory<DemoShop.SimStore>>
{
    [Fact]
    public async Task Admin_hooks_need_the_key_and_reset_keeps_the_defect_switches()
    {
        var c = f.CreateClient();
        Assert.Contains("Demo Supply Co.", await c.GetStringAsync("/"));
        Assert.NotEmpty(JsonNode.Parse(await c.GetStringAsync("/api/products"))!["items"]!.AsArray());
        Assert.Equal(8, JsonNode.Parse(await c.GetStringAsync("/api/stories"))!.AsArray().Count);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/admin/defects")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsync("/api/test/reset", null)).StatusCode);

        c.DefaultRequestHeaders.Authorization = new("Bearer", TestKeys.Demo);
        await c.PutAsJsonAsync("/api/admin/defects", new Dictionary<string, bool> { ["tax-rounding"] = true });
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync("/api/test/reset", null)).StatusCode);
        var defects = JsonNode.Parse(await c.GetStringAsync("/api/admin/defects"))!.AsArray();
        Assert.True(defects.Single(d => d!["id"]!.GetValue<string>() == "tax-rounding")!["enabled"]!.GetValue<bool>());
    }
}

public class ModelRoutingTests
{
    [Fact]
    public async Task Each_model_selection_reaches_the_adapter_that_speaks_its_protocol()
    {
        var http = new HttpClient();
        BugHunter.Api.Models.IModelProvider[] adapters = [new BugHunter.Api.Models.OpenAiCompatibleProvider(http), new BugHunter.Api.Models.AnthropicProvider(http), new BugHunter.Api.Models.OllamaProvider(http)];
        var gateway = new BugHunter.Api.Models.ModelGateway(adapters, new BugHunter.Api.Models.ModelOptions());
        var ollama = await gateway.ResolveAsync("ollama:qwen2.5:7b", default);
        // Registered first, the OpenAI-compatible adapter must not claim Ollama: it would post to /chat/completions (404).
        Assert.IsType<BugHunter.Api.Models.OllamaProvider>(adapters.First(a => a.Supports(ollama.Provider)));
        Assert.Equal("qwen2.5:7b", ollama.Model);
        Assert.IsType<BugHunter.Api.Models.AnthropicProvider>(adapters.First(a => a.Supports("Anthropic")));
        Assert.IsType<BugHunter.Api.Models.OpenAiCompatibleProvider>(adapters.First(a => a.Supports("Gemini")));
        await Assert.ThrowsAsync<BugHunter.Api.Models.ProviderExecutionException>(() => gateway.ResolveAsync("nonsense:model", default));
    }
}
