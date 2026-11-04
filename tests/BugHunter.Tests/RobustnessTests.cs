using BugHunter.Api.Hunter;
using BugHunter.Api.Models;

namespace BugHunter.Tests;

public class RobustnessTests
{
    // A provider that cuts off its first reply, the way Gemini 2.5 does when thinking uses up the token limit.
    class CutOffOnce : IModelProvider
    {
        public List<int> Limits { get; } = [];
        public bool Supports(string provider) => provider == "Fake";
        public Task<ChatResponse> CompleteAsync(ResolvedModel m, ChatRequest r, CancellationToken ct)
        {
            Limits.Add(r.MaxTokens);
            return Task.FromResult(Limits.Count == 1 ? new ChatResponse("{\"invari", 100, 1500, "fake", Truncated: true) : new ChatResponse("{}", 100, 600, "fake"));
        }
        public async IAsyncEnumerable<string> StreamAsync(ResolvedModel m, ChatRequest r, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct) { await Task.CompletedTask; yield break; }
    }

    [Fact]
    public async Task A_reply_cut_off_at_the_token_limit_is_retried_once_with_more_room()
    {
        var fake = new CutOffOnce();
        var gateway = new ModelGateway([fake], new ModelOptions());
        var res = await gateway.CompleteAsync(new ResolvedModel(null, "Fake", "", "fake", ""), new ChatRequest("s", "u", 0, 1500, [], "t", null), default);
        Assert.Equal([1500, 3000], fake.Limits);
        Assert.False(res.Truncated);
        Assert.Equal("{}", res.Text);
        Assert.Equal(2100, res.TokensOut); // both calls are counted
    }

    [Fact]
    public void The_strategist_picks_states_by_number_or_exact_name_never_by_partial_match()
    {
        string[] listed = ["/shop/", "/shop/#/cart [signed in]", "/shop/#/checkout [signed in]"];
        Assert.Equal("/shop/#/cart [signed in]", AppMap.Match("S2", listed));
        Assert.Equal("/shop/#/checkout [signed in]", AppMap.Match("s3 (checkout)", listed));
        Assert.Equal("/shop/#/cart [signed in]", AppMap.Match("\"/SHOP/#/CART [SIGNED IN]\"", listed));
        Assert.Null(AppMap.Match("S9", listed));
        Assert.Null(AppMap.Match("cart", listed)); // the old matcher sent this (and everything else) to "/shop/"
    }

    [Fact]
    public void Replays_find_labels_whose_counts_changed()
    {
        var page = new PageSnapshot { Elements = [new() { Ref = 7, Role = "link", Name = "Cart (2)" }, new() { Ref = 8, Role = "button", Name = "Pay $97.19" }] };
        Assert.Equal(7, Replayer.Resolve(page, new ElementInfo { Role = "link", Name = "Cart (0)" })!.Ref);
        Assert.Equal(8, Replayer.Resolve(page, new ElementInfo { Role = "button", Name = "Pay $12.00" })!.Ref);
        Assert.Null(Replayer.Resolve(page, new ElementInfo { Role = "link", Name = "Orders (0)" }));
    }
}
