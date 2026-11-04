using BugHunter.Api.Hunter;

namespace BugHunter.Tests;

public class ReplayTests
{
    static List<HuntStep> Steps(int n) => Enumerable.Range(1, n).Select(i => new HuntStep { N = i, Action = new HuntAction { Type = "click" } }).ToList();

    [Fact]
    public async Task Shrinking_keeps_only_the_steps_the_bug_needs()
    {
        // The bug needs steps 3 and 9 (say: sign in, then apply a coupon twice).
        var calls = 0;
        var minimal = await Replayer.ShrinkAsync(Steps(12), s => { calls++; return Task.FromResult(s.Any(x => x.N == 3) && s.Any(x => x.N == 9)); }, budget: 30);
        Assert.Equal([3, 9], minimal.Select(s => s.N));
        Assert.True(calls <= 30);
    }

    [Fact]
    public async Task Shrinking_stops_at_the_replay_budget()
    {
        var calls = 0;
        var minimal = await Replayer.ShrinkAsync(Steps(40), s => { calls++; return Task.FromResult(s.Any(x => x.N == 7)); }, budget: 3);
        Assert.Equal(3, calls);
        Assert.Contains(minimal, s => s.N == 7);
        Assert.True(minimal.Count < 40);
    }

    [Fact]
    public void Fills_are_grouped_with_the_action_that_submits_them()
    {
        var steps = new[] { "fill", "fill", "click", "navigate", "select", "press", "request" }
            .Select((t, i) => new HuntStep { N = i + 1, Action = new HuntAction { Type = t } }).ToList();
        Assert.Equal([[1, 2, 3], [4], [5, 6], [7]], Replayer.Actions(steps).Select(u => u.Select(s => s.N).ToArray()).ToArray());
    }

    [Fact]
    public void Elements_are_found_again_by_role_name_and_position()
    {
        var page = new PageSnapshot
        {
            Elements =
            [
                new() { Ref = 41, Role = "button", Name = "Add to cart" },
                new() { Ref = 42, Role = "button", Name = "Add to cart" },
                new() { Ref = 43, Role = "textbox", Name = "", Label = "Coupon code" },
            ]
        };
        Assert.Equal(42, Replayer.Resolve(page, new ElementInfo { Role = "button", Name = "Add to cart", Nth = 1 })!.Ref);
        Assert.Equal(42, Replayer.Resolve(page, new ElementInfo { Role = "button", Name = "Add to cart", Nth = 5 })!.Ref); // clamped
        Assert.Equal(43, Replayer.Resolve(page, new ElementInfo { Role = "textbox", Name = "x", Label = "Coupon code" })!.Ref);
        Assert.Null(Replayer.Resolve(page, new ElementInfo { Role = "link", Name = "Checkout" }));
    }

    [Fact]
    public void Repros_and_specs_use_the_minimal_steps_once_shrunk()
    {
        var h = new Hunt { Url = "http://shop.test/" };
        h.Steps.Add(new HuntStep { N = 0, Action = new HuntAction { Type = "open", Url = "http://shop.test/" } });
        foreach (var (n, name) in new[] { (1, "Browse"), (2, "Sign in"), (3, "Shoes"), (4, "Apply coupon") })
            h.Steps.Add(new HuntStep { N = n, Action = new HuntAction { Type = "click" }, Target = new ElementInfo { Role = "button", Name = name } });
        var f = new Finding { Id = "F1", Oracle = "duplicate-promotion", Title = "x", Step = 4, Expected = "", Actual = "", MinimalSteps = [2, 4] };
        Assert.Equal(["Open http://shop.test/", "click button \"Sign in\"", "click button \"Apply coupon\""], SpecWriter.Repro(h, f));
        var spec = SpecWriter.Write(h, f);
        Assert.Contains("'Sign in'", spec);
        Assert.DoesNotContain("'Browse'", spec);
    }

    [Fact]
    public void IsolateSecrets_sub_addresses_emails_for_partitioned_replays()
    {
        var secrets = new Dictionary<string, string>
        {
            ["email"] = "shopper01@shop.demo",
            ["password"] = "SecretPass123!",
            ["api_token"] = "tok_live_123"
        };

        var replay1 = Replayer.IsolateSecrets(secrets, "replay1");
        Assert.Equal("shopper01+replay1@shop.demo", replay1["email"]);
        Assert.Equal("SecretPass123!", replay1["password"]);
        Assert.Equal("tok_live_123", replay1["api_token"]);

        var replay2 = Replayer.IsolateSecrets(secrets, "replay2");
        Assert.Equal("shopper01+replay2@shop.demo", replay2["email"]);

        // When email already has a plus tag, base is maintained
        var alreadyTagged = new Dictionary<string, string> { ["email"] = "user+existing@example.test" };
        var isolated = Replayer.IsolateSecrets(alreadyTagged, "r1");
        Assert.Equal("user+r1@example.test", isolated["email"]);
    }
}
