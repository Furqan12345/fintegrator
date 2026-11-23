using Newtonsoft.Json.Linq;
using SimpleIPaaS.Application.Services;

namespace SimpleIPaaS.Tests.Execution;

public sealed class EmailTemplateTests
{
    private static readonly JObject FlowState = JObject.Parse("""
        {
          "MapOrder": {
            "orderNo": "A-1",
            "customer": { "name": "Ada" },
            "lines": [ { "sku": "S1" }, { "sku": "S2" } ],
            "orders": [ { "id": 1 }, { "id": 2 } ],
            "raw": { "x": 1 },
            "flag": true,
            "count": 3,
            "html": "<b>Hi</b>"
          }
        }
        """);

    private static readonly JObject PersistedState = JObject.Parse("""{ "page": "orders" }""");

    [Fact]
    public void ResolvesNestedPathAndArrayIndex()
    {
        var missing = new List<string>();

        var result = EmailTemplate.Render(
            "Order {{flowState.MapOrder.orderNo}} for {{ flowState.MapOrder.customer.name }} line {{flowState.MapOrder.lines[0].sku}}",
            FlowState,
            PersistedState,
            htmlEncode: false,
            missing);

        Assert.Equal("Order A-1 for Ada line S1", result);
        Assert.Empty(missing);
    }

    [Fact]
    public void JoinsWildcardMatchesWithComma()
    {
        var result = EmailTemplate.Render(
            "{{flowState.MapOrder.orders[*].id}}", FlowState, PersistedState, false, new List<string>());

        Assert.Equal("1, 2", result);
    }

    [Fact]
    public void ResolvesPersistedState()
    {
        var result = EmailTemplate.Render(
            "{{persistedState.page}}", FlowState, PersistedState, false, new List<string>());

        Assert.Equal("orders", result);
    }

    [Fact]
    public void MissingPathBecomesEmptyAndIsListed()
    {
        var missing = new List<string>();

        var result = EmailTemplate.Render(
            "Order {{flowState.MapOrder.nope}} synced", FlowState, PersistedState, false, missing);

        Assert.Equal("Order  synced", result);
        Assert.Equal(new[] { "flowState.MapOrder.nope" }, missing);
    }

    [Fact]
    public void ObjectIsRenderedAsCompactJson()
    {
        var result = EmailTemplate.Render(
            "{{flowState.MapOrder.raw}}", FlowState, PersistedState, false, new List<string>());

        Assert.Equal("{\"x\":1}", result);
    }

    [Fact]
    public void EscapedOpeningBracesAreLiteral()
    {
        var result = EmailTemplate.Render(
            @"\{{flowState.MapOrder.orderNo}}", FlowState, PersistedState, false, new List<string>());

        Assert.Equal("{{flowState.MapOrder.orderNo}}", result);
    }

    [Fact]
    public void HtmlEncodingAppliesOnlyWhenRequested()
    {
        var encoded = EmailTemplate.Render(
            "{{flowState.MapOrder.html}}", FlowState, PersistedState, htmlEncode: true, new List<string>());
        var raw = EmailTemplate.Render(
            "{{flowState.MapOrder.html}}", FlowState, PersistedState, htmlEncode: false, new List<string>());

        Assert.Equal("&lt;b&gt;Hi&lt;/b&gt;", encoded);
        Assert.Equal("<b>Hi</b>", raw);
    }

    [Fact]
    public void NumbersAndBooleansUseInvariantText()
    {
        var result = EmailTemplate.Render(
            "{{flowState.MapOrder.count}}/{{flowState.MapOrder.flag}}",
            FlowState,
            PersistedState,
            false,
            new List<string>());

        Assert.Equal("3/True", result);
    }
}
