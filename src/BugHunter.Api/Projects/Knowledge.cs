using System.Security.Cryptography;
using System.Text;

namespace BugHunter.Api.Projects;

// What the project's knowledge said at one sync. A story's or document's version goes up only when its content changes,
// so versions are stable whatever the source keeps (the Jira simulator and many Jira setups keep no story versions).
public class KnowledgeSnapshot
{
    public string ProjectId { get; set; } = "";
    public int Version { get; set; }
    public DateTime SyncedAt { get; set; } = DateTime.UtcNow;
    public string StorySource { get; set; } = "";
    public string DocumentSource { get; set; } = "";
    public List<KnownStory> Stories { get; set; } = [];
    public List<KnownDoc> Documents { get; set; } = [];
    public KnowledgeDiff Diff { get; set; } = new(); // against the previous snapshot
}

public record KnownStory(string Key, string Title, string Criteria, int Version, string Hash, string? Url, DateTime? SourceUpdated);
public record KnownDoc(string Id, string Title, string Text, int Version, string Hash, string? Url);

public class KnowledgeDiff
{
    public List<string> StoriesAdded { get; set; } = [];
    public List<string> StoriesRemoved { get; set; } = [];
    public List<StoryChange> StoriesChanged { get; set; } = [];
    public List<string> DocumentsAdded { get; set; } = [];
    public List<string> DocumentsRemoved { get; set; } = [];
    public List<DocChange> DocumentsChanged { get; set; } = [];
    public bool Empty => StoriesAdded.Count + StoriesRemoved.Count + StoriesChanged.Count + DocumentsAdded.Count + DocumentsRemoved.Count + DocumentsChanged.Count == 0;
}

// Criteria are compared line by line: each line is one acceptance criterion, so the diff names what changed.
public record StoryChange(string Key, int From, int To, string? TitleFrom, string? TitleTo, List<string> CriteriaAdded, List<string> CriteriaRemoved);
public record DocChange(string Id, string Title, int From, int To, List<string> LinesAdded, List<string> LinesRemoved);

public static class KnowledgeDiffer
{
    public static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(s))))[..16].ToLowerInvariant();
    static string Normalize(string s) => string.Join("\n", Lines(s));
    public static List<string> Lines(string s) => s.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    public static KnowledgeSnapshot Next(string projectId, KnowledgeSnapshot? prev, List<SourceStory> stories, List<SourceDoc> docs, string storySource, string docSource)
    {
        var snap = new KnowledgeSnapshot { ProjectId = projectId, Version = (prev?.Version ?? 0) + 1, StorySource = storySource, DocumentSource = docSource };
        var before = prev?.Stories.ToDictionary(s => s.Key) ?? [];
        foreach (var s in stories.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            var hash = Hash(s.Title + "\n" + s.Criteria);
            if (!before.TryGetValue(s.Key, out var old)) { snap.Stories.Add(new(s.Key, s.Title, s.Criteria, 1, hash, s.Url, s.Updated)); if (prev is not null) snap.Diff.StoriesAdded.Add(s.Key); continue; }
            if (old.Hash == hash) { snap.Stories.Add(old with { Url = s.Url, SourceUpdated = s.Updated }); continue; }
            snap.Stories.Add(new(s.Key, s.Title, s.Criteria, old.Version + 1, hash, s.Url, s.Updated));
            var (added, removed) = LineDiff(old.Criteria, s.Criteria);
            snap.Diff.StoriesChanged.Add(new(s.Key, old.Version, old.Version + 1, old.Title == s.Title ? null : old.Title, old.Title == s.Title ? null : s.Title, added, removed));
        }
        snap.Diff.StoriesRemoved.AddRange(before.Keys.Except(stories.Select(s => s.Key)).Order());

        var docsBefore = prev?.Documents.ToDictionary(d => d.Id) ?? [];
        foreach (var d in docs)
        {
            var hash = Hash(d.Title + "\n" + d.Text);
            if (!docsBefore.TryGetValue(d.Id, out var old)) { snap.Documents.Add(new(d.Id, d.Title, d.Text, 1, hash, d.Url)); if (prev is not null) snap.Diff.DocumentsAdded.Add(d.Title); continue; }
            if (old.Hash == hash) { snap.Documents.Add(old with { Url = d.Url }); continue; }
            snap.Documents.Add(new(d.Id, d.Title, d.Text, old.Version + 1, hash, d.Url));
            var (added, removed) = LineDiff(old.Text, d.Text);
            snap.Diff.DocumentsChanged.Add(new(d.Id, d.Title, old.Version, old.Version + 1, added, removed));
        }
        snap.Diff.DocumentsRemoved.AddRange(docsBefore.Values.Where(d => docs.All(x => x.Id != d.Id)).Select(d => d.Title));
        return snap;
    }

    static (List<string> Added, List<string> Removed) LineDiff(string from, string to)
    {
        var a = Lines(from); var b = Lines(to);
        return (b.Where(l => !a.Contains(l)).ToList(), a.Where(l => !b.Contains(l)).ToList());
    }

    // The grounding a hunt receives: every document, clipped to what the planner prompt allows.
    public static string Grounding(KnowledgeSnapshot k, int max = 8000)
    {
        var sb = new StringBuilder();
        foreach (var d in k.Documents) { sb.AppendLine($"## {d.Title}"); sb.AppendLine(d.Text.Replace($"# {d.Title}", "").Trim()); sb.AppendLine(); }
        var s = sb.ToString().Trim();
        return s.Length > max ? s[..max] : s;
    }
}
