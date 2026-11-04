using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BugHunter.Api.Hunter;

namespace BugHunter.Tests;

public class HunterOracleTests
{
    static NetworkEvent Net(string method, string url, int status, string? body = null, string? request = null) =>
        new() { Method = method, Url = url, Status = status, ResourceType = "fetch", Body = body, RequestBody = request };

    static List<Oracles.Hit> Eval(OracleState st, params NetworkEvent[] net) => Oracles.Evaluate(st, new PageSnapshot { Url = "http://shop.test/", Network = [.. net] });

    const string Products = """{"items":[{"id":"p-1","name":"Trail Running Shoes","price":89.99,"stock":3},{"id":"p-2","name":"Rain Jacket","price":139.00,"stock":5}]}""";
    static string Cart(string lines, decimal subtotal, decimal discount, decimal shipping, decimal tax, decimal total, string coupons = "") =>
        $$"""{"items":[{{lines}}],"coupons":[{{coupons}}],"subtotal":{{subtotal}},"discount":{{discount}},"shipping":{{shipping}},"tax":{{tax}},"total":{{total}}}""";

    [Fact]
    public void Consistent_cart_and_rejected_input_produce_no_findings()
    {
        var st = new OracleState();
        var hits = Eval(st,
            Net("GET", "http://shop.test/api/products", 200, Products),
            Net("PUT", "http://shop.test/api/cart/items/p-1", 200, Cart("""{"productId":"p-1","name":"Trail Running Shoes","price":89.99,"qty":2,"lineTotal":179.98}""", 179.98m, 0, 0, 14.40m, 194.38m), """{"qty":2}"""),
            Net("PUT", "http://shop.test/api/cart/items/p-2", 200, Cart("", 0, 0, 0, 0, 0), """{"qty":0}"""),
            Net("POST", "http://shop.test/api/checkout", 422, """{"title":"Your card has expired."}""", """{"expiry":"01/20"}"""),
            Net("GET", "http://shop.test/api/products?q=trail", 200, """{"items":[{"id":"p-1","name":"Trail Running Shoes","price":89.99,"stock":3}]}"""));
        Assert.Empty(hits);
        Assert.Empty(Oracles.Evaluate(st, new PageSnapshot { Console = [new("console", "Failed to load resource: the server responded with a status of 400 (Bad Request)", "x")] }));
    }

    [Fact]
    public void Seeded_shop_defects_are_each_caught_by_an_oracle()
    {
        var st = new OracleState();
        Eval(st, Net("GET", "http://shop.test/api/products", 200, Products));
        string[] keys(params NetworkEvent[] n) => Eval(st, n).Select(h => h.Key).ToArray();

        Assert.Contains("search-miss", keys(Net("GET", "http://shop.test/api/products?q=trail&category=", 200, """{"items":[]}""")));
        Assert.Contains("non-positive-qty", keys(Net("POST", "http://shop.test/api/cart/items", 200, null, """{"productId":"p-1","qty":-1}""")));
        Assert.Contains("duplicate-promotion", keys(Net("POST", "http://shop.test/api/cart/coupon", 200,
            Cart("""{"productId":"p-2","name":"Rain Jacket","price":139.00,"qty":1,"lineTotal":139.00}""", 139m, 27.80m, 0, 8.89m, 120.09m, "\"SAVE10 (10% off)\",\"SAVE10 (10% off)\""))));
        Assert.Contains("invalid-input:expired-card", keys(Net("POST", "http://shop.test/api/checkout", 201, """{"id":"ORD-2"}""", """{"cardNumber":"{{secret:card}}","expiry":"01/20"}""")));
        Assert.Contains("oversell", keys(Net("POST", "http://shop.test/api/checkout", 201, """{"id":"ORD-3","lines":[{"productId":"p-1","name":"Trail Running Shoes","price":89.99,"qty":4}]}""")));
    }

    [Fact]
    public void Tax_truncation_needs_two_independent_carts()
    {
        var st = new OracleState();
        var first = Eval(st, Net("GET", "http://shop.test/api/cart", 200, Cart("""{"productId":"p-1","name":"A","price":89.99,"qty":1,"lineTotal":89.99}""", 89.99m, 0, 0, 7.19m, 97.18m)));
        Assert.DoesNotContain(first, h => h.Key == "tax-rounding");
        var second = Eval(st, Net("GET", "http://shop.test/api/cart", 200, Cart("""{"productId":"p-1","name":"A","price":89.99,"qty":2,"lineTotal":179.98}""", 179.98m, 0, 0, 14.39m, 194.37m)));
        Assert.Contains(second, h => h.Key == "tax-rounding");
    }

    [Fact]
    public void Findings_are_reported_once()
    {
        var st = new OracleState();
        var snap = new PageSnapshot { Url = "http://shop.test/", Text = "Total: $-12.00", Console = [new("pageerror", "x is undefined", "http://shop.test/")] };
        Assert.Equal(2, Oracles.Evaluate(st, snap).Count);
        Assert.Empty(Oracles.Evaluate(st, snap));
    }
}

public class SpecWriterTests
{
    [Fact]
    public void Repro_spec_uses_semantic_locators_env_secrets_and_a_rejection_assertion()
    {
        var h = new Hunt { Id = "H-1", Url = "http://shop.test/", SecretNames = ["password"] };
        h.Steps.Add(new HuntStep { N = 0, Action = new HuntAction { Type = "open", Url = "http://shop.test/" } });
        h.Steps.Add(new HuntStep { N = 1, Action = new HuntAction { Type = "fill", Ref = 3, Value = "{{secret:password}}" }, Target = new ElementInfo { Role = "textbox", Name = "Password", Label = "Password" } });
        h.Steps.Add(new HuntStep { N = 2, Action = new HuntAction { Type = "click", Ref = 9 }, Target = new ElementInfo { Role = "button", Name = "Add to cart", Nth = 2, Dupes = 12 } });
        h.Steps.Add(new HuntStep { N = 3, Ok = false, Blocked = "irreversible", Action = new HuntAction { Type = "click", Ref = 4 }, Target = new ElementInfo { Role = "button", Name = "Pay" } });
        var f = new Finding { Id = "F1", Oracle = "non-positive-qty", Title = "Cart accepts a negative quantity", Step = 2, Expected = "Rejected", Actual = "200", RequestMethod = "POST", RequestPath = "/api/cart/items" };

        var spec = SpecWriter.Write(h, f);
        Assert.Contains("await page.goto('http://shop.test/');", spec);
        Assert.Contains("page.getByLabel('Password', { exact: true }).fill(process.env.HUNT_PASSWORD ?? '')", spec);
        Assert.Contains("page.getByRole('button', { name: 'Add to cart', exact: true }).nth(2).click()", spec);
        Assert.Contains("new URL(r.url()).pathname === '/api/cart/items'", spec);
        Assert.Contains("toBeGreaterThanOrEqual(400)", spec);
        Assert.DoesNotContain("'Pay'", spec);
        Assert.Equal(["Open http://shop.test/", "fill textbox \"Password\" with \"{{secret:password}}\"", "click button \"Add to cart\""], SpecWriter.Repro(h, new Finding { Step = 2 }));
    }
}

public class HuntPromptTests
{
    [Fact]
    public void Already_tested_summary_comes_from_recorded_steps()
    {
        var h = new Hunt();
        h.Steps.Add(new HuntStep { N = 1, Action = new HuntAction { Type = "fill", Value = "SAVE10" }, Target = new ElementInfo { Role = "textbox", Name = "Coupon code", Label = "Coupon code" } });
        h.Steps.Add(new HuntStep { N = 2, Action = new HuntAction { Type = "click" }, Requests = ["POST /api/cart/coupon → 200", "GET /api/me → 200"] });
        h.Steps.Add(new HuntStep { N = 3, Action = new HuntAction { Type = "fill", Value = "SAVE10" }, Target = new ElementInfo { Role = "textbox", Name = "Coupon code", Label = "Coupon code" } });
        h.Steps.Add(new HuntStep { N = 4, Action = new HuntAction { Type = "click" }, Requests = ["POST /api/cart/coupon → 409", "PUT /api/cart/items/p-100 → 200"] });

        var lines = ModelHuntPolicy.Tested(h);
        Assert.Contains("POST /api/cart/coupon: 200 ×1, 409 ×1", lines);
        Assert.Contains("PUT /api/cart/items/{id}: 200 ×1", lines);
        Assert.Contains("\"Coupon code\" values tried: SAVE10", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("GET"));
    }
}

public class RoutineTests
{
    // A fake browser: records actions and answers each with the page the test supplies.
    static (RoutineContext Ctx, List<HuntAction> Done, List<Oracles.Hit> Hits) Harness(PageSnapshot page, Func<HuntAction, PageSnapshot>? next = null)
    {
        var done = new List<HuntAction>(); var hits = new List<Oracles.Hit>();
        var ctx = new RoutineContext
        {
            Page = page, StepsLeft = () => 50 - done.Count, Report = hits.Add,
            Do = (a, _, _) => { done.Add(a); return Task.FromResult<(bool, string?, PageSnapshot)>((true, null, next?.Invoke(a) ?? page)); }
        };
        return (ctx, done, hits);
    }

    static PageSnapshot Shop(string text = "") => new()
    {
        Url = "http://shop.test/", Text = text,
        Elements = [
            new() { Ref = 1, Role = "searchbox", Name = "Search products", Label = "Search products" },
            new() { Ref = 2, Role = "button", Name = "Search" },
            new() { Ref = 3, Role = "spinbutton", Name = "Quantity", Label = "Quantity" },
            new() { Ref = 4, Role = "button", Name = "Add to cart" },
            new() { Ref = 5, Role = "link", Name = "Cart (0)", Href = "/#/cart" }
        ]
    };

    [Fact]
    public void Elements_are_found_by_label_or_name_exact_first()
    {
        var (ctx, _, _) = Harness(Shop());
        Assert.Equal(3, ctx.Find("quantity")!.Ref);
        Assert.Equal(2, ctx.Find("Search", "button")!.Ref);
        Assert.Equal(5, ctx.Find("cart", "link")!.Ref);
        Assert.Null(ctx.Find("Checkout"));
    }

    [Fact]
    public async Task Boundary_values_include_one_above_the_stated_stock()
    {
        var (ctx, done, _) = Harness(Shop("Adjustable Dumbbell Set · Only 3 left"));
        await Routines.Get("boundary-values")!.RunAsync(ctx, new Dictionary<string, string> { ["field"] = "Quantity", ["submit"] = "Add to cart" }, default);
        Assert.Equal(["-1", "0", "1", "4", "99999", ""], done.Where(a => a.Type == "fill").Select(a => a.Value!).ToArray());
        Assert.Equal(6, done.Count(a => a.Type == "click" && a.Ref == 4));
    }

    [Fact]
    public async Task Case_variants_report_only_when_result_counts_differ()
    {
        PageSnapshot Results(HuntAction a, int lower, int other)
        {
            var p = Shop();
            if (a.Type == "click")
            {
                var q = Last ?? "";
                p.Network = [new() { Method = "GET", Status = 200, ResourceType = "fetch", Url = $"http://shop.test/api/products?q={q}", Body = $$"""{"items":[{{string.Join(",", Enumerable.Repeat("{}", q == q.ToLowerInvariant() ? lower : other))}}]}""" }];
            }
            else Last = a.Value;
            return p;
        }
        var (bad, _, badHits) = Harness(Shop(), a => Results(a, 0, 1));
        await Routines.Get("case-variants")!.RunAsync(bad, new Dictionary<string, string> { ["field"] = "Search products", ["submit"] = "Search", ["term"] = "Trail" }, default);
        Assert.Contains(badHits, h => h.Key == "search-case-sensitive");

        var (good, _, goodHits) = Harness(Shop(), a => Results(a, 1, 1));
        await Routines.Get("case-variants")!.RunAsync(good, new Dictionary<string, string> { ["field"] = "Search products", ["submit"] = "Search", ["term"] = "Trail" }, default);
        Assert.Empty(goodHits);
    }

    static string? Last;

    [Fact]
    public async Task Missing_elements_are_reported_back_to_the_model()
    {
        var (ctx, done, _) = Harness(Shop());
        await Routines.Get("fill-form")!.RunAsync(ctx, new Dictionary<string, string> { ["Card number"] = "4242", ["submit"] = "Place order" }, default);
        Assert.Empty(done);
        Assert.Contains(ctx.Problems, p => p.Contains("Card number"));
        Assert.Contains(ctx.Problems, p => p.Contains("Place order"));
    }
}
