using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BugHunter.Api.Common;

namespace BugHunter.Api.Projects;

public record SourceStory(string Key, string Title, string Criteria, string? Url, DateTime? Updated);
public record SourceDoc(string Id, string Title, string Text, int? SourceVersion, string? Url);

// Ports for the project's knowledge. Adapters: files in a repository (built in), and Jira and Confluence over their
// Cloud REST APIs, which the simulators in src/Simulators expose with the same shapes.
public interface IStorySource { string Name { get; } Task<List<SourceStory>> StoriesAsync(CancellationToken ct); }
public interface IDocSource { string Name { get; } Task<List<SourceDoc>> DocumentsAsync(CancellationToken ct); }

public class FileStorySource(string path) : IStorySource
{
    public string Name => "files";
    public Task<List<SourceStory>> StoriesAsync(CancellationToken ct)
    {
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        return Task.FromResult((json["stories"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(s => new SourceStory(J.Text(s["key"]), J.Text(s["title"]), J.Text(s["criteria"]), null, null)).ToList());
    }
}

public class FileDocSource(string dir) : IDocSource
{
    public string Name => "files";
    public Task<List<SourceDoc>> DocumentsAsync(CancellationToken ct) => Task.FromResult(Directory.EnumerateFiles(dir, "*.md").Order().Select(f =>
    {
        var text = File.ReadAllText(f);
        var title = text.Split('\n').FirstOrDefault(l => l.StartsWith("# "))?[2..].Trim() ?? Path.GetFileNameWithoutExtension(f);
        return new SourceDoc(Path.GetFileNameWithoutExtension(f), title, text.Trim(), null, null);
    }).ToList());
}

// Atlassian Cloud: basic auth with the account's email and an API token.
public abstract class AtlassianSource(HttpClient http, string baseUrl, string email, string token)
{
    protected readonly string Base = baseUrl.TrimEnd('/');

    protected async Task<JsonNode> GetAsync(string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, Base + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{token}")));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new KnowledgeSourceException($"{GetType().Name.Replace("Source", "")} answered {(int)res.StatusCode} for {path}: {body[..Math.Min(200, body.Length)]}");
        return JsonNode.Parse(body)!;
    }
}

public class KnowledgeSourceException(string message) : Exception(message);

public class JiraStorySource(HttpClient http, string baseUrl, string projectKey, string email, string token) : AtlassianSource(http, baseUrl, email, token), IStorySource
{
    public string Name => $"Jira {projectKey}";

    public async Task<List<SourceStory>> StoriesAsync(CancellationToken ct)
    {
        var all = new List<SourceStory>();
        for (var start = 0; ; )
        {
            var page = await GetAsync($"/rest/api/3/search?jql={Uri.EscapeDataString($"project = {projectKey} AND issuetype = Story ORDER BY key")}&startAt={start}&maxResults=50", ct);
            var issues = page["issues"] as JsonArray ?? [];
            foreach (var i in issues.OfType<JsonObject>())
                all.Add(new SourceStory(J.Text(i["key"]), J.Text(i["fields"]?["summary"]), Adf.Text(i["fields"]?["description"]), $"{Base}/browse/{J.Text(i["key"])}",
                    DateTime.TryParse(J.Text(i["fields"]?["updated"]), out var u) ? u.ToUniversalTime() : null));
            start += issues.Count;
            if (issues.Count == 0 || start >= (int)(J.Num(page["total"]) ?? 0)) break;
        }
        return all;
    }
}

public class ConfluenceDocSource(HttpClient http, string baseUrl, string spaceKey, string email, string token) : AtlassianSource(http, baseUrl, email, token), IDocSource
{
    public string Name => $"Confluence {spaceKey}";

    public async Task<List<SourceDoc>> DocumentsAsync(CancellationToken ct)
    {
        var spaces = await GetAsync($"/wiki/api/v2/spaces?keys={Uri.EscapeDataString(spaceKey)}", ct);
        var id = J.Text((spaces["results"] as JsonArray)?.FirstOrDefault()?["id"]);
        if (id.Length == 0) throw new KnowledgeSourceException($"Confluence space {spaceKey} was not found.");
        var pages = await GetAsync($"/wiki/api/v2/spaces/{id}/pages?body-format=storage", ct);
        return (pages["results"] as JsonArray ?? []).OfType<JsonObject>().Select(p => new SourceDoc(J.Text(p["id"]), J.Text(p["title"]),
            StorageText(J.Text(p["body"]?["storage"]?["value"])), (int?)J.Num(p["version"]?["number"]), $"{Base}/wiki/pages/{J.Text(p["id"])}")).ToList();
    }

    // Confluence storage format (XHTML) to plain text that keeps the structure a reader needs: headings, list items, rows.
    public static string StorageText(string xhtml)
    {
        var s = Regex.Replace(xhtml, @"<h([1-6])[^>]*>", m => "\n" + new string('#', int.Parse(m.Groups[1].Value)) + " ");
        s = Regex.Replace(s, @"<li[^>]*>", "\n- ");
        s = Regex.Replace(s, @"<tr[^>]*>", "\n|");
        s = Regex.Replace(s, @"</td>|</th>", " |");
        s = Regex.Replace(s, @"</p>|</h[1-6]>|<br\s*/?>", "\n");
        s = WebUtility.HtmlDecode(Regex.Replace(s, "<[^>]+>", ""));
        return Regex.Replace(s, @"\n{3,}", "\n\n").Trim();
    }
}

// Jira Cloud sends rich text in the Atlassian document format; plain strings are accepted too.
public static class Adf
{
    public static string Text(JsonNode? n) => n switch
    {
        JsonValue v => v.ToString(),
        JsonObject o when o["content"] is JsonArray blocks => string.Join("\n", blocks.Select(Inline)),
        _ => ""
    };

    static string Inline(JsonNode? b) => b?["text"] is JsonValue t ? t.ToString() : string.Concat((b?["content"] as JsonArray ?? []).Select(Inline));

    public static JsonObject Doc(string text) => new()
    {
        ["type"] = "doc", ["version"] = 1,
        ["content"] = new JsonArray(text.Split('\n').Select(line => (JsonNode)new JsonObject
        {
            ["type"] = "paragraph", ["content"] = line.Length == 0 ? new JsonArray() : new JsonArray(new JsonObject { ["type"] = "text", ["text"] = line })
        }).ToArray())
    };
}

public class KnowledgeSources(IHttpClientFactory http, ProjectStore store, Hunter.HunterOptions opts)
{
    public async Task<(IStorySource? Stories, IDocSource? Docs)> ForAsync(Project p, CancellationToken ct)
    {
        var secrets = await store.SecretsAsync(p.Id, ct);
        var gt = DemoProjects.FindGroundTruth(AppContext.BaseDirectory) ?? DemoProjects.FindGroundTruth(Directory.GetCurrentDirectory());
        foreach (var c in new[] { p.Knowledge.Stories, p.Knowledge.Documents })
        {
            if (c.Kind == "files" && UrlGuard.CheckLocalPath(c.Path, gt) is { } pathErr) throw new KnowledgeSourceException(pathErr);
            if (c.Kind is "jira" or "confluence" && await UrlGuard.CheckAsync(c.BaseUrl ?? "", opts.AllowPrivateTargets == true, allowHttp: true) is { } urlErr) throw new KnowledgeSourceException(urlErr);
        }
        string Token(SourceConfig c) => c.TokenSecret is { } t && secrets.TryGetValue(t, out var v) ? v : throw new KnowledgeSourceException($"The project secret '{c.TokenSecret}' for {c.Kind} is not set.");
        IStorySource? stories = p.Knowledge.Stories.Kind switch
        {
            "files" => new FileStorySource(p.Knowledge.Stories.Path!),
            "jira" => new JiraStorySource(http.CreateClient(), p.Knowledge.Stories.BaseUrl!, p.Knowledge.Stories.Key!, p.Knowledge.Stories.Email ?? "", Token(p.Knowledge.Stories)),
            _ => null
        };
        IDocSource? docs = p.Knowledge.Documents.Kind switch
        {
            "files" => new FileDocSource(p.Knowledge.Documents.Path!),
            "confluence" => new ConfluenceDocSource(http.CreateClient(), p.Knowledge.Documents.BaseUrl!, p.Knowledge.Documents.Key!, p.Knowledge.Documents.Email ?? "", Token(p.Knowledge.Documents)),
            _ => null
        };
        return (stories, docs);
    }
}
