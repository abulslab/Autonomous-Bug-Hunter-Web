using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BugHunter.Api.Hunter;

namespace BugHunter.Tests;

public class InvariantTests
{
    static NetworkEvent Net(string method, string url, int status, string? body = null, string? request = null) =>
        new() { Method = method, Url = url, Status = status, ResourceType = "fetch", Body = body, RequestBody = request };

    static Invariant Rule(string on, string check)
    {
        var (r, err) = InvariantRules.Create(null, on, check, null, null, "User", 0);
        Assert.True(r is not null, err);
        return r!;
    }

    const string Cart = """{"items":[{"productId":"p-1","price":24.5,"qty":2,"lineTotal":49.0},{"productId":"p-2","price":10,"qty":1,"lineTotal":10}],"subtotal":59.0,"discount":5,"shipping":0,"tax":4.32,"total":58.32}""";

    [Fact]
    public void Rules_hold_break_or_skip_on_recorded_responses()
    {
        var cart = Net("GET", "http://shop.test/shop/x/api/cart", 200, Cart);
        Assert.True(InvariantRules.Evaluate(Rule("GET /api/cart", "total == subtotal - discount + shipping + tax"), cart, null).Holds);
        Assert.True(InvariantRules.Evaluate(Rule("GET /api/cart", "subtotal == sum(items, price * qty) && all(items, lineTotal == price * qty)"), cart, null).Holds);
        Assert.True(InvariantRules.Evaluate(Rule("GET /api/cart", "tax == round((subtotal - discount) * 0.08, 2)"), cart, null).Holds);
        var broken = InvariantRules.Evaluate(Rule("GET /api/cart", "discount == 0 and count(items) > 5"), cart, null);
        Assert.False(broken.Holds);
        Assert.Equal("5", broken.Values["discount"]);
        // A field the response does not have means the rule does not apply here, not that it failed.
        Assert.Null(InvariantRules.Evaluate(Rule("*", "balance >= 0"), cart, null).Holds);
        // JavaScript arrows, as smaller models write them, mean the list item.
        Assert.True(InvariantRules.Evaluate(Rule("GET /api/cart", "subtotal == sum(items, item => item.price * item.qty)"), cart, null).Holds);
        Assert.True(InvariantRules.Evaluate(Rule("GET /api/cart", "all(items, (x) => x.qty >= 1)"), cart, null).Holds);
    }

    [Fact]
    public void Rules_check_requests_status_query_dates_and_the_previous_response()
    {
        var neg = Net("POST", "http://shop.test/api/cart/items", 200, "{}", """{"productId":"p-1","qty":-1}""");
        var rule = Rule("POST /api/cart/items", "status >= 400 || request.qty >= 1");
        Assert.True(InvariantRules.Applies(rule, neg));
        Assert.False(InvariantRules.Evaluate(rule, neg, null).Holds);
        Assert.True(InvariantRules.Evaluate(rule, Net("POST", "http://shop.test/api/cart/items", 400, "{}", """{"qty":-1}"""), null).Holds);

        var expired = Net("POST", "http://shop.test/api/checkout", 201, """{"id":"o-1"}""", """{"expiry":"01/20"}""");
        Assert.False(InvariantRules.Evaluate(Rule("POST /api/checkout", "status >= 400 || date(request.expiry) >= today()"), expired, null).Holds);

        var search = Net("GET", "http://shop.test/api/products?q=Trail", 200, """{"items":[{"name":"Trail Shoes"},{"name":"Trail Map"}]}""");
        Assert.True(InvariantRules.Evaluate(Rule("GET /api/products", "all(items, contains(lower(name), lower(query.q)))"), search, null).Holds);

        var before = Net("GET", "http://shop.test/api/products/p-1", 200, """{"stock":5}""");
        var after = Net("GET", "http://shop.test/api/products/p-1", 200, """{"stock":6}""");
        Assert.False(InvariantRules.Evaluate(Rule("GET /api/products/{id}", "stock <= prev.stock"), after, before).Holds);
        Assert.Null(InvariantRules.Evaluate(Rule("GET /api/products/{id}", "stock <= prev.stock"), after, null).Holds);
    }

    [Fact]
    public void Matching_and_parsing_are_strict_about_shape_and_lenient_about_spelling()
    {
        var items = Net("PUT", "http://shop.test/shop/x/api/cart/items/p-1", 200, "{}");
        Assert.True(InvariantRules.Applies(Rule("PUT /api/cart/items/{id}", "status < 500"), items));
        Assert.True(InvariantRules.Applies(Rule("* /api/cart/items/*", "status < 500"), items));
        Assert.False(InvariantRules.Applies(Rule("GET /api/cart/items/{id}", "status < 500"), items));
        Assert.False(InvariantRules.Applies(Rule("PUT /api/cart", "status < 500"), items));
        Assert.True(InvariantRules.Applies(Rule("PUT http://shop.test/shop/x/api/cart/items/{id}", "status < 500"), items));

        Assert.NotNull(InvariantRules.Create(null, "*", "a = b AND NOT c", null, null, "Model", 0).Rule); // "=", and/not
        Assert.Null(InvariantRules.Create(null, "*", "total ==", null, null, "Model", 0).Rule);
        Assert.Null(InvariantRules.Create(null, "*", "1 + 1 == 2", null, null, "Model", 0).Rule); // refers to no field
        Assert.Null(InvariantRules.Create(null, "*", "explode(total)", null, null, "Model", 0).Rule);
        Assert.Null(InvariantRules.Create(null, "GET api cart", "total > 0", null, null, "Model", 0).Rule);

        var (rules, errors) = InvariantRules.ParseLines("GET /api/cart: total >= 0  # Totals are never negative\n\nsubtotal >= discount\nPOST /x: (");
        Assert.Equal(2, rules.Count);
        Assert.Equal("Totals are never negative", rules[0].Claim);
        Assert.Equal("*", rules[1].On);
        Assert.Single(errors);
    }

    [Fact]
    public void Broken_rules_become_specs_that_assert_the_rule()
    {
        Assert.Equal("eq(body?.total, ((body?.subtotal - body?.discount) + body?.tax))", InvariantRules.ToJs("total == subtotal - discount + tax"));
        Assert.Equal("((res.status() >= 400) || (req?.qty >= 1))", InvariantRules.ToJs("status >= 400 || request.qty >= 1"));
        Assert.Contains("every((it) =>", InvariantRules.ToJs("all(items, qty >= 1)"));
        Assert.Null(InvariantRules.ToJs("stock <= prev.stock")); // no previous response in a replay

        var h = new Hunt { Url = "http://shop.test/" };
        h.Steps.Add(new HuntStep { N = 0, Action = new HuntAction { Type = "open", Url = "http://shop.test/" } });
        h.Steps.Add(new HuntStep { N = 1, Action = new HuntAction { Type = "click" }, Target = new ElementInfo { Role = "button", Name = "Apply" } });
        var spec = SpecWriter.Write(h, new Finding
        {
            Id = "F1", Oracle = "invariant", Provenance = "Invariant", Title = "Rule broken: discount capped", Step = 1, Expected = "x", Actual = "y",
            RequestMethod = "GET", RequestPath = "/api/cart", Rule = "discount <= subtotal", Requirement = "DEMO-121"
        });
        Assert.Contains("waitForResponse", spec);
        Assert.Contains("expect((body?.discount <= body?.subtotal), 'DEMO-121: Rule broken: discount capped').toBe(true);", spec);
    }
}

