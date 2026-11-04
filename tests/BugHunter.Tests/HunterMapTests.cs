using BugHunter.Api.Hunter;

namespace BugHunter.Tests;

public class AppMapTests
{
    static PageSnapshot Page(string url, bool signedIn = false) =>
        new() { Url = url, Elements = signedIn ? [new() { Role = "button", Name = "Sign out" }] : [new() { Role = "link", Name = "Sign in" }] };

    static List<HuntStep> Click(string name, params (string Label, string Value)[] fills) =>
        [.. fills.Select(f => new HuntStep { Action = new HuntAction { Type = "fill", Value = f.Value }, Target = new ElementInfo { Role = "textbox", Name = f.Label } }),
         new HuntStep { Action = new HuntAction { Type = "click" }, Target = new ElementInfo { Role = "button", Name = name } }];

    [Fact]
    public void States_fold_ids_and_include_sign_in()
    {
        Assert.Equal("/shop/#/product/{id}", AppMap.StateOf(Page("http://x.test/shop/#/product/p-101")));
        Assert.Equal(AppMap.StateOf(Page("http://x.test/shop/#/product/p-101")), AppMap.StateOf(Page("http://x.test/shop/#/product/p-207")));
        Assert.Equal("/shop/#/cart [signed in]", AppMap.StateOf(Page("http://x.test/shop/#/cart?x=1", signedIn: true)));
        Assert.Equal(AppMap.StateOf(Page("http://x.test/shop/")), AppMap.StateOf(Page("http://x.test/shop/#/")));
    }

    [Fact]
    public void The_navigator_takes_the_shortest_known_road_and_avoids_failing_ones()
    {
        var map = new AppMap();
        map.Learn("/", Click("Sign in", ("Email", "{{secret:email}}"), ("Password", "{{secret:password}}")), "/ [signed in]");
        map.Learn("/ [signed in]", Click("Shoes"), "/product/{id} [signed in]");
        map.Learn("/product/{id} [signed in]", Click("Add to cart"), "/cart [signed in]");
        map.Learn("/ [signed in]", Click("Cart"), "/cart [signed in]");

        var route = map.Route("/", "/cart [signed in]")!;
        Assert.Equal(["Sign in", "Cart"], route.Select(r => r.Actions[^1].Target!.Name));
        Assert.Equal(3, route[0].Actions.Count); // the fills travel with the click that submits them

        map.Roads.Single(r => r.Signature.Contains("\"Cart\"")).Failed = 5;
        Assert.Equal(["Sign in", "Shoes", "Add to cart"], map.Route("/", "/cart [signed in]")!.Select(r => r.Actions[^1].Target!.Name));
        Assert.Null(map.Route("/cart [signed in]", "/"));
        Assert.Contains("/cart [signed in]", map.Reachable("/"));
    }

    [Fact]
    public void Roads_are_learned_only_from_actions_that_worked_and_changed_state()
    {
        var map = new AppMap();
        map.Learn("/cart", Click("Update"), "/cart");
        var failed = Click("Checkout"); failed[0].Ok = false;
        map.Learn("/cart", failed, "/checkout");
        Assert.Empty(map.Roads);
        map.Learn("/cart", Click("Checkout"), "/checkout");
        map.Learn("/cart", Click("Checkout"), "/checkout");
        Assert.Equal(2, Assert.Single(map.Roads).Ok);
    }
}
