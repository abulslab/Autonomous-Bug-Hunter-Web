using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BugHunter.Api.Hunter;

namespace BugHunter.Tests;

public class ProbeTests
{
    static NetworkEvent Net(int rid, string method, string url, int status, string? body = null, string? request = null) =>
        new() { Rid = rid, Method = method, Url = url, Status = status, ResourceType = "fetch", Body = body, RequestBody = request };

    static (Hunt, OracleState) Recorded(bool irreversible)
    {
        var h = new Hunt { Url = "http://shop.test/", AllowIrreversible = irreversible };
        var st = new OracleState();
        st.Stock["p-1"] = 3;
        var steps = Enumerable.Range(0, 5).Select(n => new HuntStep { N = n, Action = new HuntAction { Type = "click" } }).ToList();
        h.Steps.AddRange(steps);
        st.History.Add((steps[1], Net(1, "GET", "http://shop.test/api/products?q=Trail", 200, """{"items":[]}""")));
        st.History.Add((steps[2], Net(2, "POST", "http://shop.test/api/cart/items", 200, "{}", """{"productId":"p-1","qty":1}""")));
        st.History.Add((steps[3], Net(3, "POST", "http://shop.test/api/checkout", 201, """{"id":"ORD-1"}""", """{"cardNumber":"4242424242424242","expiry":"12/30","name":"{{secret:name}}"}""")));
        st.History.Add((steps[4], Net(4, "POST", "http://shop.test/api/login", 200, "{}", """{"email":"a@b.test"}""")));
        return (h, st);
    }

    [Fact]
    public void The_engine_plans_probes_from_recorded_calls_without_a_model()
    {
        var (h, st) = Recorded(irreversible: true);
        var plan = Probes.Plan(h, st);
        string[] labels = [.. plan.Select(p => $"{Probes.Endpoint(p.Base)} | {p.Label}")];

        Assert.Contains("GET /api/products | q=\"trail\"", labels);
        Assert.Contains("GET /api/products | q=\"TRAIL\"", labels);
        Assert.Contains("POST /api/cart/items | qty = -1", labels);
        Assert.Contains("POST /api/cart/items | qty = 4 (stock is 3)", labels);
        Assert.Contains("POST /api/checkout | place the order with that quantity", labels);
        Assert.Contains("POST /api/cart/items | send the same request twice", labels);
        Assert.Contains("POST /api/checkout | expiry = 01/20 (in the past)", labels);
        Assert.Contains("POST /api/checkout | cardNumber fails the Luhn check", labels);
        Assert.Contains("POST /api/cart/items | send without the session", labels);
        // Sign-in calls are never probed; orders are never sent twice; secrets are never changed.
        Assert.DoesNotContain(labels, l => l.StartsWith("POST /api/login"));
        Assert.DoesNotContain("POST /api/checkout | send the same request twice", labels);
        Assert.DoesNotContain(labels, l => l.Contains("name ="));
        // Every order probe first puts an item back in the cart.
        Assert.All(plan.Where(p => p.Base.Path.EndsWith("/checkout") && p.Body is not null), p => Assert.NotNull(p.Setup));
        // Numbers stay numbers in the changed body.
        Assert.Contains(plan, p => p.Body == """{"productId":"p-1","qty":-1}""");
    }

    [Fact]
    public void Order_calls_are_left_alone_unless_the_hunt_allows_irreversible_actions()
    {
        var (h, st) = Recorded(irreversible: false);
        Assert.DoesNotContain(Probes.Plan(h, st), p => p.Base.Path.EndsWith("/checkout"));
    }

    [Fact]
    public void Model_probes_only_use_recorded_calls_and_fields()
    {
        var (h, st) = Recorded(irreversible: true);
        var g = 0;
        Assert.Equal(2, Probes.FromModel(h, st, "POST /api/cart/items", "qty", ["99", "100"], "story says at most 99", null, ref g).Count);
        Assert.Empty(Probes.FromModel(h, st, "POST /api/cart/items", "price", ["0"], null, null, ref g));      // the call has no such field
        Assert.Empty(Probes.FromModel(h, st, "POST /api/wishlist", "qty", ["1"], null, null, ref g));        // never recorded
    }

    [Fact]
    public void Probe_findings_get_a_spec_that_reuses_the_apps_own_request()
    {
        var h = new Hunt { Url = "http://shop.test/" };
        h.Steps.Add(new HuntStep { N = 0, Action = new HuntAction { Type = "open", Url = "http://shop.test/" } });
        h.Steps.Add(new HuntStep { N = 1, Action = new HuntAction { Type = "click" }, Target = new ElementInfo { Role = "button", Name = "Add to cart" } });
        h.Steps.Add(new HuntStep
        {
            N = 2, Driver = "Probe", Routine = "boundary",
            Action = new HuntAction { Type = "request", Key = "POST", Url = "http://shop.test/api/cart/items", Value = """{"qty":-1}""", Routine = "boundary",
                Args = new() { ["label"] = "qty = -1", ["group"] = "A1", ["base"] = "1", ["path"] = "/api/cart/items", ["dropAuth"] = "false" } }
        });
        var spec = SpecWriter.Write(h, new Finding { Id = "F1", Oracle = "non-positive-qty", Provenance = "Observed", Title = "Negative quantity accepted", Step = 2, Expected = "x", Actual = "y" });
        Assert.Contains("page.on('request'", spec);
        Assert.Contains("getByRole('button', { name: 'Add to cart', exact: true }).click()", spec);
        Assert.Contains("res = await send('POST /api/cart/items', null, '{\"qty\":-1}');", spec);
        Assert.Contains("expect(res.status(), 'the server should reject this request').toBeGreaterThanOrEqual(400);", spec);
    }
}

