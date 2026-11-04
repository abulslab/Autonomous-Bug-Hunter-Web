using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Simulators;

public record Issue(string Id, string Key, string Project, string Type, string Summary, string Description, string Status, List<string> Labels,
    DateTime Created, DateTime Updated, List<Comment> Comments, List<Change> History);
public record Comment(string Id, string Body, DateTime Created);
public record Change(string Id, DateTime Created, string Field, string? From, string? To);
public record Space(string Id, string Key, string Name);
public record IssueLink(string Id, string Type, string Inward, string Outward);
public record PageVersion(int Number, string Title, string Storage, DateTime CreatedAt);
public record Page(string Id, string SpaceId, List<PageVersion> Versions)
{
    public PageVersion Current => Versions[^1];
}

// The simulated Jira and Confluence content, seeded from ground-truth/<app>/knowledge/ (stories.json and pages/*.md).
// Everything is in memory; /sim/reset reseeds it.
public partial class SimData
{
    static readonly DateTime Seeded = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
    readonly Lock _lock = new();
    readonly string _dir;
    public readonly Dictionary<string, Issue> Issues = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, Space> Spaces = new();
    public readonly Dictionary<string, Page> Pages = new();
    public readonly List<IssueLink> Links = [];
    int _ids;

    public SimData(string groundTruthDir) { _dir = groundTruthDir; Reset(); }

    public static string FindGroundTruth(string start)
    {
        for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "ground-truth"))) return Path.Combine(d.FullName, "ground-truth");
        throw new InvalidOperationException($"No ground-truth folder above {start}; set Simulators:GroundTruthDir.");
    }

    public void Reset()
    {
        lock (_lock)
        {
            Issues.Clear(); Spaces.Clear(); Pages.Clear(); Links.Clear(); _ids = 10000;
            foreach (var app in Directory.EnumerateDirectories(_dir).Order())
            {
                var stories = Path.Combine(app, "knowledge", "stories.json");
                if (!File.Exists(stories)) continue;
                var seed = JsonNode.Parse(File.ReadAllText(stories))!;
                var project = seed["project"]!.GetValue<string>();
                foreach (var s in seed["stories"]!.AsArray())
                {
                    var key = s!["key"]!.GetValue<string>();
                    Issues[key] = new Issue(Next(), key, project, "Story", s["title"]!.GetValue<string>(), s["criteria"]!.GetValue<string>(), "Done",
                        ["acceptance-criteria"], Seeded, Seeded, [], []);
                }
                var space = new Space(Next(), project, $"{Path.GetFileName(app)} product documentation");
                Spaces[space.Id] = space;
                foreach (var md in Directory.EnumerateFiles(Path.Combine(app, "knowledge", "pages"), "*.md").Order())
                {
                    var text = File.ReadAllText(md);
                    var title = text.Split('\n').FirstOrDefault(l => l.StartsWith("# "))?[2..].Trim() ?? Path.GetFileNameWithoutExtension(md);
                    var page = new Page(Next(), space.Id, [new PageVersion(1, title, Storage(text), Seeded)]);
                    Pages[page.Id] = page;
                }
            }
        }
    }

    string Next() => (++_ids).ToString();

    // ---- Jira ----
    public Issue Create(string project, string type, string summary, string description, List<string> labels)
    {
        lock (_lock)
        {
            var n = Issues.Values.Where(i => i.Project.Equals(project, StringComparison.OrdinalIgnoreCase))
                .Select(i => int.TryParse(i.Key.Split('-').Last(), out var x) ? x : 0).DefaultIfEmpty(0).Max() + 1;
            var now = DateTime.UtcNow;
            var issue = new Issue(Next(), $"{project.ToUpperInvariant()}-{n}", project.ToUpperInvariant(), type, summary, description, "To Do", labels, now, now, [], []);
            Issues[issue.Key] = issue;
            return issue;
        }
    }

    public Issue Edit(Issue i, string? summary, string? description, List<string>? labels)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow; var history = i.History;
            if (summary is not null && summary != i.Summary) history.Add(new Change(Next(), now, "summary", i.Summary, summary));
            if (description is not null && description != i.Description) history.Add(new Change(Next(), now, "description", i.Description, description));
            return Issues[i.Key] = i with { Summary = summary ?? i.Summary, Description = description ?? i.Description, Labels = labels ?? i.Labels, Updated = now };
        }
    }

    public static readonly Dictionary<string, string> Transitions = new() { ["11"] = "To Do", ["21"] = "In Progress", ["31"] = "Done" };

    public Issue Transition(Issue i, string status)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            i.History.Add(new Change(Next(), now, "status", i.Status, status));
            return Issues[i.Key] = i with { Status = status, Updated = now };
        }
    }

    public Comment AddComment(Issue i, string body)
    {
        lock (_lock)
        {
            var c = new Comment(Next(), body, DateTime.UtcNow);
            i.Comments.Add(c);
            Issues[i.Key] = i with { Updated = c.Created };
            return c;
        }
    }

    // Atlassian document format: Jira Cloud's v3 API sends and takes rich text as ADF; a paragraph per line here.
    public static JsonObject Adf(string text) => new()
    {
        ["type"] = "doc", ["version"] = 1,
        ["content"] = new JsonArray(text.Split('\n').Select(line => (JsonNode)new JsonObject
        {
            ["type"] = "paragraph",
            ["content"] = line.Length == 0 ? new JsonArray() : new JsonArray(new JsonObject { ["type"] = "text", ["text"] = line })
        }).ToArray())
    };

    public static string FromAdf(JsonNode? n) => n switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonObject o when o["type"]?.ToString() == "doc" => string.Join("\n", (o["content"] as JsonArray ?? []).Select(Block)),
        _ => ""
    };

    static string Block(JsonNode? b) => string.Concat((b?["content"] as JsonArray ?? []).Select(t => t?["text"]?.ToString() ?? Block(t)));

    public IssueLink Link(string type, string inward, string outward)
    {
        lock (_lock) { var l = new IssueLink(Next(), type, inward, outward); Links.Add(l); return l; }
    }

    // ---- Confluence ----
    public Space CreateSpace(string key, string name)
    {
        lock (_lock) { var s = new Space(Next(), key.ToUpperInvariant(), name); Spaces[s.Id] = s; return s; }
    }

    public Page CreatePage(string spaceId, string title, string storage)
    {
        lock (_lock) { var p = new Page(Next(), spaceId, [new PageVersion(1, title, storage, DateTime.UtcNow)]); Pages[p.Id] = p; return p; }
    }

    public Page EditPage(Page p, string title, string storage)
    {
        lock (_lock)
        {
            p.Versions.Add(new PageVersion(p.Current.Number + 1, title, storage, DateTime.UtcNow));
            return p;
        }
    }

    // Markdown (headings, lists, tables, paragraphs, bold) to Confluence storage format (XHTML).
    public static string Storage(string md)
    {
        var sb = new StringBuilder(); var inList = false; var table = new List<string>();
        void Flush()
        {
            if (inList) { sb.Append("</ul>"); inList = false; }
            if (table.Count > 0)
            {
                sb.Append("<table><tbody>");
                foreach (var row in table.Where(r => !Regex.IsMatch(r, @"^\|[\s\-|]+\|$")))
                    sb.Append("<tr>").Append(string.Concat(row.Trim('|').Split('|').Select(c => $"<td>{Inline(c.Trim())}</td>"))).Append("</tr>");
                sb.Append("</tbody></table>");
                table.Clear();
            }
        }
        foreach (var raw in md.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith('|')) { if (inList) Flush(); table.Add(line); continue; }
            if (table.Count > 0) Flush();
            if (line.StartsWith("- ")) { if (!inList) { sb.Append("<ul>"); inList = true; } sb.Append($"<li>{Inline(line[2..])}</li>"); continue; }
            if (line.StartsWith("  ") && inList) { sb.Insert(sb.Length - 5, " " + Inline(line.Trim())); continue; } // a wrapped list item
            Flush();
            if (line.StartsWith("## ")) sb.Append($"<h2>{Inline(line[3..])}</h2>");
            else if (line.StartsWith("# ")) sb.Append($"<h1>{Inline(line[2..])}</h1>");
            else if (line.Length > 0) sb.Append($"<p>{Inline(line)}</p>");
        }
        Flush();
        return sb.ToString();
    }

    static string Inline(string s) => Bold().Replace(WebUtility.HtmlEncode(s), "<strong>$1</strong>");

    [GeneratedRegex(@"\*\*(.+?)\*\*")] private static partial Regex Bold();

    public static JsonObject Json(object o) => JsonSerializer.SerializeToNode(o, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
}
