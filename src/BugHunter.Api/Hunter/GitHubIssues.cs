using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using BugHunter.Api.Common;

namespace BugHunter.Api.Hunter;

public class GitHubOptions
{
    public string? Token { get; set; }  // GITHUB_TOKEN: needs "issues: write" on the repository
    public string? Repo { get; set; }   // "owner/name"
    public string ApiUrl { get; set; } = "https://api.github.com";
}

// Files a finding as a GitHub issue. Each issue carries a fingerprint (site + check), so the same bug found again in a
// later hunt adds a comment to the open issue instead of a duplicate.
public class GitHubIssues(IHttpClientFactory http, GitHubOptions opts)
{
    public bool Enabled => !string.IsNullOrWhiteSpace(Token) && !string.IsNullOrWhiteSpace(opts.Repo);
    public string? Repo => opts.Repo;
    string? Token => !string.IsNullOrWhiteSpace(opts.Token) ? opts.Token : Environment.GetEnvironmentVariable("GITHUB_TOKEN");
    const string Label = "bug-hunter";

    public static string Fingerprint(Hunt h, Finding f) => $"{(Uri.TryCreate(h.Url, UriKind.Absolute, out var u) ? u.Host : h.Url)}|{f.Key}";
    static string Marker(string fingerprint) => $"<!-- bughunter-fingerprint: {fingerprint} -->";

    HttpRequestMessage Req(HttpMethod method, string path, object? body = null)
    {
        var r = new HttpRequestMessage(method, opts.ApiUrl.TrimEnd('/') + path);
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        r.Headers.UserAgent.ParseAdd("autonomous-bug-hunter");
        r.Headers.Accept.ParseAdd("application/vnd.github+json");
        if (body is not null) r.Content = JsonContent.Create(body);
        return r;
    }

    async Task<JsonNode?> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var client = http.CreateClient();
        using var res = await client.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"GitHub returned {(int)res.StatusCode}: {(text.Length > 200 ? text[..200] : text)}");
        return text.Length == 0 ? null : JsonNode.Parse(text);
    }

    public async Task<(string Url, bool Created)> FileAsync(Hunt h, Finding f, CancellationToken ct)
    {
        if (!Enabled) throw new InvalidOperationException("GitHub filing is not configured: set GITHUB_TOKEN and GitHub:Repo.");
        var fingerprint = Fingerprint(h, f);
        var open = await SendAsync(Req(HttpMethod.Get, $"/repos/{opts.Repo}/issues?state=open&labels={Label}&per_page=100"), ct) as JsonArray ?? [];
        if (open.OfType<JsonObject>().FirstOrDefault(i => J.Text(i["body"]).Contains(Marker(fingerprint))) is { } existing)
        {
            await SendAsync(Req(HttpMethod.Post, $"/repos/{opts.Repo}/issues/{existing["number"]}/comments",
                new { body = $"Seen again in hunt `{h.Id}` ({h.CreatedAt:yyyy-MM-dd}): {(f.Reproduced == "Confirmed" ? $"reproduced {f.ReplayPasses}/{f.ReplayRuns} without AI" : f.Reproduced ?? "not replayed")}.\n\nActual: {f.Actual}" }), ct);
            return (J.Text(existing["html_url"]), false);
        }
        var created = await SendAsync(Req(HttpMethod.Post, $"/repos/{opts.Repo}/issues", new
        {
            title = $"[Bug Hunter] {f.Title}",
            body = Body(h, f) + "\n\n" + Marker(fingerprint),
            labels = new[] { Label, $"severity:{f.Severity.ToLowerInvariant()}" }
        }), ct);
        return (J.Text(created?["html_url"]), true);
    }

    public static string Body(Hunt h, Finding f)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**{f.Severity}** · {(f.Provenance == "Observed" ? "Proven by an oracle" : f.Provenance == "Invariant" ? "Rule broken" : "AI suspicion (needs triage)")} · " +
            (f.Reproduced == "Confirmed" ? $"reproduced {f.ReplayPasses}/{f.ReplayRuns} without AI" : f.Reproduced ?? "not replayed") + (f.Requirement is { } r ? $" · {r}" : ""));
        sb.AppendLine();
        sb.AppendLine($"**Expected:** {f.Expected}");
        sb.AppendLine();
        sb.AppendLine($"**Actual:** {f.Actual}");
        sb.AppendLine();
        sb.AppendLine($"**Where:** {f.Url} (hunt `{h.Id}`, step {f.Step})");
        sb.AppendLine();
        if (f.Repro.Count > 0)
        {
            sb.AppendLine("### Steps to reproduce");
            for (var i = 0; i < f.Repro.Count; i++) sb.AppendLine($"{i + 1}. {f.Repro[i]}");
            sb.AppendLine();
        }
        if (f.Spec is { } spec) { sb.AppendLine("### Playwright test"); sb.AppendLine("```ts"); sb.AppendLine(spec.TrimEnd()); sb.AppendLine("```"); }
        return sb.ToString();
    }
}
