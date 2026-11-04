using System.Net;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using BugHunter.Api.Hunter;

namespace BugHunter.Tests;

public class ReportingTests
{
    static Hunt Sample()
    {
        var h = new Hunt { Id = "H-test", Url = "http://shop.test/", Status = "Completed", Strategy = "Guided", MaxSteps = 40 };
        h.Usage.CostUsd = 0.15; h.Usage.CostPerConfirmedBug = 0.05;
        h.Findings.Add(new Finding { Id = "F1", Key = "non-positive-qty", Oracle = "non-positive-qty", Provenance = "Observed", Severity = "High", Title = "Cart accepts a negative quantity",
            Expected = "Quantities below 1 are rejected.", Actual = "qty = -1 returned HTTP 200.", Url = "http://shop.test/#/cart", Reproduced = "Confirmed", ReplayPasses = 2, ReplayRuns = 2, Repro = ["Open http://shop.test/", "click button \"Add to cart\""] });
        h.Findings.Add(new Finding { Id = "F2", Key = "judged:x", Oracle = "model-judgement", Provenance = "Judged", Severity = "Low", Title = "Button looks enabled", Expected = "e", Actual = "a", Url = "http://shop.test/" });
        h.Intents.Add(new Intent { Id = "I1", Goal = "Apply a coupon", Status = "Violated", Success = ["Discount never exceeds subtotal"], Priority = 1 });
        return h;
    }

    [Fact]
    public void Pipelines_fail_only_on_reproduced_evidence()
    {
        var junit = XDocument.Parse(Reports.JUnit(Sample()));
        var suite = junit.Root!.Element("testsuite")!;
        Assert.Equal("3", suite.Attribute("tests")!.Value);
        Assert.Equal("2", suite.Attribute("failures")!.Value); // the confirmed finding and the violated intent
        Assert.Equal("1", suite.Attribute("skipped")!.Value);  // the AI suspicion never fails a build

        var sarif = Reports.Sarif(Sample());
        var results = sarif["runs"]![0]!["results"]!.AsArray();
        Assert.Equal("error", results[0]!["level"]!.GetValue<string>());
        Assert.Equal("note", results[1]!["level"]!.GetValue<string>());
        Assert.Equal("shop.test|non-positive-qty", results[0]!["partialFingerprints"]!["bugHunter/v1"]!.GetValue<string>());
    }

    [Fact]
    public void Human_reports_lead_with_confirmed_findings()
    {
        var md = Reports.Markdown(Sample());
        Assert.True(md.IndexOf("F1: Cart accepts a negative quantity") < md.IndexOf("F2: Button looks enabled"));
        Assert.Contains("Reproduced 2/2", md);
        Assert.Contains("AI suspicion (needs triage)", md);
        var html = Reports.Html(Sample());
        Assert.Contains("<h1>Bug Hunter report: shop.test</h1>", html);
        Assert.DoesNotContain("<script", html);
    }

    class FakeGitHub : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];
        public string? ExistingBody { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Calls.Add($"{r.Method} {r.RequestUri!.PathAndQuery}");
            var json = r.Method == HttpMethod.Get
                ? (ExistingBody is null ? "[]" : new JsonArray(new JsonObject { ["number"] = 7, ["html_url"] = "https://github.com/o/r/issues/7", ["body"] = ExistingBody }).ToJsonString())
                : """{"html_url":"https://github.com/o/r/issues/8"}""";
            await Task.CompletedTask;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }

    class Factory(HttpMessageHandler handler) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(handler, false); }

    [Fact]
    public async Task The_same_bug_found_again_comments_on_its_open_issue_instead_of_duplicating()
    {
        var h = Sample(); var f = h.Findings[0];
        var fake = new FakeGitHub();
        var github = new GitHubIssues(new Factory(fake), new GitHubOptions { Token = "t", Repo = "o/r" });

        var (url, created) = await github.FileAsync(h, f, default);
        Assert.True(created);
        Assert.Equal("https://github.com/o/r/issues/8", url);
        Assert.Contains("POST /repos/o/r/issues", fake.Calls);

        fake.Calls.Clear();
        fake.ExistingBody = "...\n<!-- bughunter-fingerprint: shop.test|non-positive-qty -->";
        (url, created) = await github.FileAsync(h, f, default);
        Assert.False(created);
        Assert.Equal("https://github.com/o/r/issues/7", url);
        Assert.Contains("POST /repos/o/r/issues/7/comments", fake.Calls);
    }
}
