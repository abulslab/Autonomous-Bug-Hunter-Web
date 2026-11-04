using System.Text.RegularExpressions;

namespace BugHunter.Api.Hunter;

public record SkillDto(string? Title, string[]? Match, string? Body);

// Skill packs over the API: the built-in packs (skills/*.md, reviewed with the code) and the team's own, which are kept
// in the data directory so they reach every hunt without being committed by accident.
public static partial class SkillEndpoints
{
    public static void MapSkills(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/skills").WithTags("Skill packs");

        g.MapGet("", (HunterOptions opts) => Results.Ok(SkillPacks.LoadAll(opts).Select(p => new { p.Name, p.Title, p.Match, p.Body, p.Custom })))
            .WithSummary("The skill packs the planner can be given: built-in and custom");

        g.MapPut("/{name}", async (string name, SkillDto d, HunterOptions opts, CancellationToken ct) =>
        {
            if (!Name().IsMatch(name)) return Results.Problem("A skill pack name uses 2 to 40 lower-case letters, digits and '-'.", statusCode: 400);
            var match = (d.Match ?? []).Select(m => m.Trim().ToLowerInvariant()).Where(m => m.Length > 0).Distinct().ToArray();
            if (string.IsNullOrWhiteSpace(d.Title) || match.Length < 2 || string.IsNullOrWhiteSpace(d.Body))
                return Results.Problem("A skill pack needs a title, at least two match keywords (a pack applies when two appear in the app) and a body.", statusCode: 400);
            if (d.Body!.Length > 20_000) return Results.Problem("The body is at most 20,000 characters.", statusCode: 400);
            if (opts.CustomSkillsDir is null) return Results.Problem("No directory for custom skill packs is configured.", statusCode: 500);
            Directory.CreateDirectory(opts.CustomSkillsDir);
            await File.WriteAllTextAsync(Path.Combine(opts.CustomSkillsDir, name + ".md"), SkillPacks.Markdown(name, d.Title!.Trim(), match, d.Body), ct);
            return Results.Ok(SkillPacks.LoadAll(opts).First(p => p.Name == name));
        }).WithSummary("Create or replace a custom skill pack");

        g.MapDelete("/{name}", (string name, HunterOptions opts) =>
        {
            var file = opts.CustomSkillsDir is null || !Name().IsMatch(name) ? null : Path.Combine(opts.CustomSkillsDir, name + ".md");
            if (file is null || !File.Exists(file)) return Results.Problem("Only custom skill packs can be deleted; built-in packs live in skills/ with the code.", statusCode: 404);
            File.Delete(file);
            return Results.NoContent();
        }).WithSummary("Delete a custom skill pack");
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{1,39}$")] private static partial Regex Name();
}
