using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace BugHunter.Api.Hunter;

// A hunt as a document for people (Markdown, HTML) or for pipelines (SARIF for code-scanning dashboards, JUnit for any
// CI test view). Confirmed findings come first everywhere; suspicions are labelled as such and never fail a build.
public static class Reports
{
    static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;
    static IEnumerable<Finding> Ordered(Hunt h) => h.Findings.OrderBy(f => f.Reproduced == "Confirmed" ? 0 : f.Provenance == "Judged" ? 3 : f.Reproduced is null ? 1 : 2);
    static string Usd(double v) => v == 0 ? "$0" : v < 0.01 ? $"${v:0.0000}" : $"${v:0.00}";
    static string Kind(Finding f) => f.Provenance switch { "Observed" => "Proven by an oracle", "Invariant" => "Rule broken", _ => "AI suspicion (needs triage)" };
    static string Replay(Finding f) => f.Reproduced switch { "Confirmed" => $"Reproduced {f.ReplayPasses}/{f.ReplayRuns}", "Flaky" => $"Flaky {f.ReplayPasses}/{f.ReplayRuns}", "NotReproduced" => "Not reproduced", _ => "Not replayed" };

    public static string Markdown(Hunt h)
    {
        var sb = new StringBuilder();
        var confirmed = h.Findings.Count(f => f.Reproduced == "Confirmed");
        sb.AppendLine($"# Bug Hunter report: {Host(h.Url)}");
        sb.AppendLine();
        sb.AppendLine($"Hunt `{h.Id}` · {h.Status} · {h.CreatedAt:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine();
        sb.AppendLine("| | |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| Target | {h.Url} |");
        sb.AppendLine($"| Strategy | {h.Strategy}{(h.Model is { } m && h.Policy != "Heuristic" ? $" · {m.Model}" : "")}{(h.PlannerModel is { } p ? $" + planner {p.Model}" : "")} |");
        sb.AppendLine($"| Confirmed bugs | **{confirmed}** (of {h.Findings.Count(f => f.Provenance != "Judged")} proven; {h.Findings.Count(f => f.Provenance == "Judged")} suspicions) |");
        sb.AppendLine($"| Steps | {h.Steps.Count(s => s.N > 0 && s.Driver != "Probe")} of {h.MaxSteps}, plus {h.ProbeRequests} probe requests |");
        sb.AppendLine($"| AI cost | {Usd(h.Usage.CostUsd)} ({h.Usage.ModelCalls} calls){(h.Usage.CostPerConfirmedBug is { } c ? $", {Usd(c)} per confirmed bug" : "")} |");
        if (h.StartedAt is { } s) sb.AppendLine($"| Duration | {((h.FinishedAt ?? DateTime.UtcNow) - s).TotalMinutes:0.0} minutes |");
        if (h.Requirements.Count + h.SkillPacks.Count > 0)
            sb.AppendLine($"| Grounding | {(h.Requirements.Count > 0 ? $"stories {string.Join(", ", h.Requirements.Select(r => r.Key))}" : "no stories")}; {(h.SkillPacks.Count > 0 ? $"skill packs {string.Join(", ", h.SkillPacks)}" : "no skill packs")} |");
        if (h.Benchmark is { Total: > 0 } b) sb.AppendLine($"| Benchmark | {b.Detected} of {b.Total} seeded defects found, {b.ConfirmedDetected} confirmed, {b.UnmatchedObserved} unexplained |");
        sb.AppendLine();

        if (h.Intents.Count > 0)
        {
            sb.AppendLine("## Intents");
            sb.AppendLine();
            sb.AppendLine("| Intent | Story | Status | Variants tested | Findings |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var i in h.Intents.OrderBy(i => i.Priority))
                sb.AppendLine($"| {i.Goal} | {i.Requirement ?? "-"} | {i.Status} | {i.Variants.Count(v => v.Status == "Tested")} / {i.Variants.Count} | {(i.FindingIds.Count > 0 ? string.Join(", ", i.FindingIds) : "-")} |");
            sb.AppendLine();
        }

        sb.AppendLine("## Findings");
        sb.AppendLine();
        if (h.Findings.Count == 0) sb.AppendLine("No findings: nothing was proven broken.");
        foreach (var f in Ordered(h))
        {
            sb.AppendLine($"### {f.Id}: {f.Title}");
            sb.AppendLine();
            sb.AppendLine($"**{f.Severity}** · {Kind(f)} · {Replay(f)}{(f.Requirement is { } r ? $" · {r}" : "")}");
            sb.AppendLine();
            sb.AppendLine($"- **Expected:** {f.Expected}");
            sb.AppendLine($"- **Actual:** {f.Actual}");
            sb.AppendLine($"- **Where:** step {f.Step} on {f.Url}");
            if (f.MinimalSteps is { } min) sb.AppendLine($"- **Repro:** shrunk from {f.TrailSteps} steps to {min.Count}");
            sb.AppendLine();
            if (f.Repro.Count > 0)
            {
                sb.AppendLine("Steps to reproduce:");
                sb.AppendLine();
                for (var k = 0; k < f.Repro.Count; k++) sb.AppendLine($"{k + 1}. {f.Repro[k]}");
                sb.AppendLine();
            }
            if (f.Spec is { } spec)
            {
                sb.AppendLine("<details><summary>Playwright test</summary>");
                sb.AppendLine();
                sb.AppendLine("```ts");
                sb.AppendLine(spec.TrimEnd());
                sb.AppendLine("```");
                sb.AppendLine();
                sb.AppendLine("</details>");
                sb.AppendLine();
            }
        }

        if (h.Invariants.Count > 0)
        {
            sb.AppendLine("## Rules checked on every response");
            sb.AppendLine();
            sb.AppendLine("| Rule | Applies to | From | Result |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var r in h.Invariants)
                sb.AppendLine($"| {r.Claim}<br>`{r.Check.Replace("|", "\\|")}` | `{r.On}` | {r.Intent ?? r.Requirement ?? (r.Source == "User" ? "user" : "-")} | {(r.Status == "Violated" ? "Broken" : r.Status == "Holding" ? "Holds" : "Not exercised")} ({r.Holds}/{r.Checks}) |");
            sb.AppendLine();
        }

        sb.AppendLine("## Hunt log");
        sb.AppendLine();
        foreach (var n in h.Notes) sb.AppendLine($"- {n}");
        sb.AppendLine();
        sb.AppendLine("_Generated by Autonomous Bug Hunter. The AI proposes; the engine verifies: every proven finding fired on recorded evidence, and confirmed findings reproduced on replay without AI._");
        return sb.ToString();
    }

    // A self-contained page (no scripts, no external assets) suitable for attaching to a ticket or a release.
    public static string Html(Hunt h)
    {
        var md = Markdown(h);
        var body = new StringBuilder();
        var inCode = false; var inTable = false; var inList = false;
        void CloseBlocks() { if (inTable) { body.AppendLine("</table>"); inTable = false; } if (inList) { body.AppendLine("</ul>"); inList = false; } }
        foreach (var raw in md.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("```")) { CloseBlocks(); body.AppendLine(inCode ? "</pre>" : "<pre>"); inCode = !inCode; continue; }
            if (inCode) { body.AppendLine(WebUtility.HtmlEncode(line)); continue; }
            if (line.StartsWith("<details>") || line.StartsWith("</details>")) { CloseBlocks(); body.AppendLine(line); continue; }
            if (line.StartsWith('|'))
            {
                if (line.StartsWith("|---")) continue;
                if (!inTable) { CloseBlocks(); body.AppendLine("<table>"); inTable = true; }
                var cells = line.Trim('|').Split(" | ");
                body.AppendLine("<tr>" + string.Concat(cells.Select(c => $"<td>{Inline(c.Trim())}</td>")) + "</tr>");
                continue;
            }
            if (line.StartsWith("- ") || System.Text.RegularExpressions.Regex.IsMatch(line, @"^\d+\. "))
            {
                if (!inList) { CloseBlocks(); body.AppendLine("<ul>"); inList = true; }
                body.AppendLine($"<li>{Inline(System.Text.RegularExpressions.Regex.Replace(line, @"^(- |\d+\. )", ""))}</li>");
                continue;
            }
            CloseBlocks();
            if (line.StartsWith("### ")) body.AppendLine($"<h3>{Inline(line[4..])}</h3>");
            else if (line.StartsWith("## ")) body.AppendLine($"<h2>{Inline(line[3..])}</h2>");
            else if (line.StartsWith("# ")) body.AppendLine($"<h1>{Inline(line[2..])}</h1>");
            else if (line.Length > 0) body.AppendLine($"<p>{Inline(line)}</p>");
        }
        CloseBlocks();
        return $$"""
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Bug Hunter report {{h.Id}}</title>
<style>
body{font:14px/1.55 -apple-system,"Segoe UI",Roboto,Inter,sans-serif;color:#0f172a;background:#fff;max-width:960px;margin:40px auto;padding:0 24px}
h1{font-size:22px;margin:0 0 4px}h2{font-size:16px;margin:32px 0 8px;padding-bottom:6px;border-bottom:1px solid #e2e8f0}h3{font-size:14px;margin:24px 0 4px}
table{border-collapse:collapse;width:100%;margin:8px 0}td{border-bottom:1px solid #e2e8f0;padding:6px 8px;vertical-align:top}
code,pre{font-family:"JetBrains Mono",ui-monospace,monospace;font-size:12px}pre{background:#f8fafc;border:1px solid #e2e8f0;border-radius:6px;padding:12px;overflow:auto}
p,li{color:#334155}summary{cursor:pointer;color:#475569}
</style></head><body>
{{body}}
</body></html>
""";
    }

    static string Inline(string s)
    {
        var e = WebUtility.HtmlEncode(s).Replace("&lt;br&gt;", "<br>");
        e = System.Text.RegularExpressions.Regex.Replace(e, @"\*\*(.+?)\*\*", "<strong>$1</strong>");
        e = System.Text.RegularExpressions.Regex.Replace(e, @"`(.+?)`", "<code>$1</code>");
        return e.Replace("\\|", "|");
    }

    // SARIF 2.1.0: one rule per check kind, one result per finding. Suspicions are notes, never errors.
    public static JsonObject Sarif(Hunt h)
    {
        var results = new JsonArray();
        var rules = new Dictionary<string, JsonObject>();
        foreach (var f in Ordered(h))
        {
            var ruleId = f.Provenance == "Invariant" ? $"rule/{f.Key.Replace("invariant:", "")}" : f.Oracle;
            rules.TryAdd(ruleId, new JsonObject { ["id"] = ruleId, ["name"] = f.Oracle, ["shortDescription"] = new JsonObject { ["text"] = f.Provenance == "Invariant" ? f.Title : f.Oracle } });
            var level = f.Provenance == "Judged" || f.Reproduced is "NotReproduced" ? "note" : f.Severity is "High" or "Critical" ? "error" : "warning";
            results.Add(new JsonObject
            {
                ["ruleId"] = ruleId,
                ["level"] = level,
                ["message"] = new JsonObject { ["text"] = $"{f.Title}. Expected: {f.Expected} Actual: {f.Actual}" },
                ["locations"] = new JsonArray(new JsonObject { ["physicalLocation"] = new JsonObject { ["artifactLocation"] = new JsonObject { ["uri"] = f.Url } } }),
                ["partialFingerprints"] = new JsonObject { ["bugHunter/v1"] = $"{Host(h.Url)}|{f.Key}" },
                ["properties"] = new JsonObject
                {
                    ["finding"] = f.Id, ["provenance"] = f.Provenance, ["severity"] = f.Severity, ["reproduced"] = f.Reproduced, ["requirement"] = f.Requirement,
                    ["steps"] = new JsonArray(f.Repro.Select(r => (JsonNode)r).ToArray()), ["playwright"] = f.Spec
                }
            });
        }
        return new JsonObject
        {
            ["$schema"] = "https://json.schemastore.org/sarif-2.1.0.json",
            ["version"] = "2.1.0",
            ["runs"] = new JsonArray(new JsonObject
            {
                ["tool"] = new JsonObject { ["driver"] = new JsonObject { ["name"] = "Autonomous Bug Hunter", ["informationUri"] = "https://github.com/abulslab/Autonomous-Bug-Hunter-Web", ["rules"] = new JsonArray(rules.Values.ToArray<JsonNode>()) } },
                ["results"] = results,
                ["properties"] = new JsonObject { ["hunt"] = h.Id, ["target"] = h.Url, ["costUsd"] = h.Usage.CostUsd }
            })
        };
    }

    // JUnit: each intent and each proven finding is a test case. Confirmed findings and violated intents fail;
    // suspicions and unconfirmed findings are skipped, so a pipeline fails only on reproduced evidence.
    public static string JUnit(Hunt h)
    {
        var cases = new List<XElement>();
        foreach (var i in h.Intents.OrderBy(i => i.Priority))
        {
            var c = new XElement("testcase", new XAttribute("classname", "intents"), new XAttribute("name", $"{i.Id} {i.Goal}"));
            if (i.Status == "Violated") c.Add(new XElement("failure", new XAttribute("message", "A success check broke"), string.Join("\n", i.Success)));
            else if (i.Status is "Unreachable" or "Skipped" or "Failed") c.Add(new XElement("skipped", new XAttribute("message", i.Status)));
            cases.Add(c);
        }
        foreach (var f in Ordered(h))
        {
            var c = new XElement("testcase", new XAttribute("classname", $"findings.{f.Oracle}"), new XAttribute("name", $"{f.Id} {f.Title}"));
            if (f.Reproduced == "Confirmed" || (f.Reproduced is null && f.Provenance != "Judged"))
                c.Add(new XElement("failure", new XAttribute("message", f.Title), new XAttribute("type", f.Severity), $"Expected: {f.Expected}\nActual: {f.Actual}\n\n{string.Join("\n", f.Repro)}"));
            else c.Add(new XElement("skipped", new XAttribute("message", f.Provenance == "Judged" ? "AI suspicion, needs triage" : Replay(f))));
            cases.Add(c);
        }
        var suite = new XElement("testsuite",
            new XAttribute("name", $"Bug Hunter {h.Id} {Host(h.Url)}"),
            new XAttribute("tests", cases.Count),
            new XAttribute("failures", cases.Count(c => c.Element("failure") is not null)),
            new XAttribute("skipped", cases.Count(c => c.Element("skipped") is not null)),
            new XAttribute("timestamp", h.CreatedAt.ToString("s")),
            cases);
        return new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement("testsuites", suite)).ToString();
    }
}
