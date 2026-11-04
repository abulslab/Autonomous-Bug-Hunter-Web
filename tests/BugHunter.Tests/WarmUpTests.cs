using BugHunter.Api.Hunter;

namespace BugHunter.Tests;

public class WarmUpTests
{
    // Found from this source file, so the test works whatever folder the build output is in.
    static string SkillsDir([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        for (var d = new DirectoryInfo(Path.GetDirectoryName(here)!); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "skills"))) return Path.Combine(d.FullName, "skills");
        throw new DirectoryNotFoundException("skills/");
    }

    [Fact]
    public void Skill_packs_load_from_files_and_match_what_the_survey_saw()
    {
        var packs = SkillPacks.Load(SkillsDir());
        Assert.Contains(packs, p => p.Name == "ecommerce" && p.Body.Contains("## Risks"));
        Assert.DoesNotContain(packs, p => p.Name == "README");

        var shop = SkillPacks.Select(packs, "Demo Supply Co. Search products Search Add to cart Checkout Coupon code Only 3 left Sign in Sign out GET /api/products POST /api/cart/items POST /api/login");
        Assert.Equal("ecommerce", shop[0].Name);
        Assert.Contains(shop, p => p.Name == "search-and-lists" || p.Name == "authentication");
        // One keyword alone is not enough: a blog that mentions "order" once is not a shop.
        Assert.Empty(SkillPacks.Select(packs, "In order to write good posts..."));
    }

    [Fact]
    public void The_api_catalog_records_fields_and_statuses_from_traffic()
    {
        var map = new AppMap();
        NetworkEvent Net(string method, string url, int status, string? body, string? req = null) =>
            new() { Method = method, Url = url, Status = status, ResourceType = "fetch", Body = body, RequestBody = req };
        ApiCatalog.Learn(map, [
            Net("GET", "http://shop.test/api/cart", 200, """{"items":[{"productId":"p-1","price":24.5,"qty":2}],"subtotal":49,"total":52.92}"""),
            Net("POST", "http://shop.test/api/cart/items", 200, "{}", """{"productId":"p-1","qty":1}"""),
            Net("POST", "http://shop.test/api/cart/items", 400, """{"title":"Quantity must be at least 1."}""", """{"productId":"p-1","qty":-1}"""),
        ]);
        var cart = map.Apis.Single(a => a.Endpoint == "GET /api/cart");
        Assert.Contains("items[].price", cart.ResponseFields);
        Assert.Contains("total", cart.ResponseFields);
        var add = map.Apis.Single(a => a.Endpoint == "POST /api/cart/items");
        Assert.Equal([200, 400], add.Statuses);
        Assert.Equal(["productId", "qty"], add.RequestFields);
        Assert.DoesNotContain("title", add.ResponseFields); // error bodies are not the endpoint's shape
        Assert.Contains("POST /api/cart/items [200,400] request: productId, qty", ApiCatalog.Describe(map, 10));
    }
}
