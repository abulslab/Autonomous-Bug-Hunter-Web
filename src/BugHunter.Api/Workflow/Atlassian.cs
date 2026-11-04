using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using BugHunter.Api.Common;
using BugHunter.Api.Hunter;
using BugHunter.Api.Projects;

namespace BugHunter.Api.Workflow;

// Writes to Jira and Confluence (Cloud REST APIs; the simulators in src/Simulators expose the same shapes): bugs filed
// and linked to the stories they break, and the readiness report published as a page.
public class AtlassianWriter(HttpClient http, string baseUrl, string email, string token)
{
    readonly string _base = baseUrl.TrimEnd('/');

    async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, _base + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{token}")));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null) req.Content = JsonContent.Create(body);
        using var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new KnowledgeSourceException($"{method} {path} answered {(int)res.StatusCode}: {text[..Math.Min(200, text.Length)]}");
        return text.Length == 0 ? null : JsonNode.Parse(text);
    }

    public string Browse(string key) => $"{_base}/browse/{key}";

    public static string BugDescription(Hunt h, Finding f, string? story) => string.Join("\n", new[]
    {
        $"Found by Bug Hunter in hunt {h.Id} ({h.Environment ?? "?"}, role {h.Role ?? "default"}); {f.Provenance}, {(f.Reproduced == "Confirmed" ? $"reproduced {f.ReplayPasses}/{f.ReplayRuns} times without AI" : f.Reproduced ?? "not replayed")}.",
        story is null ? "" : $"Breaks story {story}.",
        "", $"Expected: {f.Expected}", $"Actual: {f.Actual}", $"Where: {f.RequestMethod} {f.RequestPath} on {f.Url}",
        "", "Steps to reproduce:",
    }.Concat(f.Repro.Select((s, i) => $"{i + 1}. {s}")));

    public async Task<string> FileBugAsync(string project, string summary, string description, IEnumerable<string> labels, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["fields"] = new JsonObject
            {
                ["project"] = new JsonObject { ["key"] = project }, ["issuetype"] = new JsonObject { ["name"] = "Bug" },
                ["summary"] = summary.Length > 250 ? summary[..250] : summary, ["description"] = Adf.Doc(description),
                ["labels"] = new JsonArray(labels.Select(l => (JsonNode)l).ToArray()),
            }
        };
        return J.Text((await SendAsync(HttpMethod.Post, "/rest/api/3/issue", body, ct))?["key"]);
    }

    public Task LinkAsync(string bug, string story, CancellationToken ct) => SendAsync(HttpMethod.Post, "/rest/api/3/issueLink",
        new JsonObject { ["type"] = new JsonObject { ["name"] = "Relates" }, ["inwardIssue"] = new JsonObject { ["key"] = bug }, ["outwardIssue"] = new JsonObject { ["key"] = story } }, ct);

    public Task CommentAsync(string key, string text, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"/rest/api/3/issue/{key}/comment", new JsonObject { ["body"] = Adf.Doc(text) }, ct);

    // Creates the space and the page if missing; otherwise a new version of the page (optimistic locking).
    public async Task<(string Id, int Version)> PublishAsync(string spaceKey, string title, string storage, CancellationToken ct)
    {
        var spaces = await SendAsync(HttpMethod.Get, $"/wiki/api/v2/spaces?keys={Uri.EscapeDataString(spaceKey)}", null, ct);
        var spaceId = J.Text((spaces?["results"] as JsonArray)?.FirstOrDefault()?["id"]);
        if (spaceId.Length == 0) spaceId = J.Text((await SendAsync(HttpMethod.Post, "/wiki/api/v2/spaces", new JsonObject { ["key"] = spaceKey, ["name"] = "Quality reports" }, ct))?["id"]);
        var pages = await SendAsync(HttpMethod.Get, $"/wiki/api/v2/spaces/{spaceId}/pages", null, ct);
        var existing = (pages?["results"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(p => J.Text(p["title"]) == title);
        var body = new JsonObject { ["representation"] = "storage", ["value"] = storage };
        if (existing is null)
        {
            var created = await SendAsync(HttpMethod.Post, "/wiki/api/v2/pages", new JsonObject { ["spaceId"] = spaceId, ["status"] = "current", ["title"] = title, ["body"] = body }, ct);
            return (J.Text(created?["id"]), 1);
        }
        var next = (int)(J.Num(existing["version"]?["number"]) ?? 1) + 1;
        await SendAsync(HttpMethod.Put, $"/wiki/api/v2/pages/{J.Text(existing["id"])}", new JsonObject { ["id"] = J.Text(existing["id"]), ["status"] = "current", ["title"] = title, ["body"] = body, ["version"] = new JsonObject { ["number"] = next } }, ct);
        return (J.Text(existing["id"]), next);
    }

    // Markdown (headings, lists, tables, bold, paragraphs) to Confluence storage format.
    public static string Storage(string md)
    {
        static string Inline(string s) => System.Text.RegularExpressions.Regex.Replace(System.Net.WebUtility.HtmlEncode(s), @"\*\*(.+?)\*\*", "<strong>$1</strong>");
        var sb = new StringBuilder(); var list = false; var rows = new List<string[]>();
        void Flush()
        {
            if (list) { sb.Append("</ul>"); list = false; }
            if (rows.Count == 0) return;
            sb.Append("<table><tbody>");
            for (var i = 0; i < rows.Count; i++) sb.Append("<tr>").Append(string.Concat(rows[i].Select(c => i == 0 ? $"<th>{Inline(c)}</th>" : $"<td>{Inline(c)}</td>"))).Append("</tr>");
            sb.Append("</tbody></table>"); rows.Clear();
        }
        foreach (var raw in md.Replace("\r", "").Split('\n'))
        {
            var l = raw.TrimEnd();
            if (l.StartsWith('|')) { if (list) { sb.Append("</ul>"); list = false; } if (!l.Replace("|", "").Replace("-", "").Trim().Equals("")) rows.Add(l.Trim('|').Split('|').Select(c => c.Trim()).ToArray()); continue; }
            if (rows.Count > 0) Flush();
            if (l.StartsWith("- ")) { if (!list) { sb.Append("<ul>"); list = true; } sb.Append($"<li>{Inline(l[2..])}</li>"); continue; }
            Flush();
            if (l.StartsWith('#')) { var n = l.TakeWhile(c => c == '#').Count(); sb.Append($"<h{n}>{Inline(l[n..].Trim())}</h{n}>"); }
            else if (l.Length > 0) sb.Append($"<p>{Inline(l)}</p>");
        }
        Flush();
        return sb.ToString();
    }
}
