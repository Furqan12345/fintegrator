using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Xunit;
using SimpleIPaaS.Application.Models;

namespace SimpleIPaaS.Tests.Execution;

public sealed class FlowTestContextTests
{
    [Fact]
    public void ResolvesStaticAndDynamicFixtureTokens()
    {
        var executionId = Guid.NewGuid();
        var definition = new FlowTestDefinition
        {
            Variables = new Dictionary<string, string> { ["account"] = "demo" },
            TriggerPayloadJson = "{\"account\":\"{{account}}\",\"execution\":\"{{run.id}}\"}"
        };
        var context = FlowTestContext.FromJson(JsonConvert.SerializeObject(definition), executionId);

        var payload = context.ResolveTriggerPayload(null);

        Assert.Contains("\"account\":\"demo\"", payload);
        Assert.Contains(executionId.ToString(), payload);
    }

    [Fact]
    public void UsesSeededCrossReferenceRowsAndKeepsLastHttpResponse()
    {
        var stepId = Guid.NewGuid();
        var definition = new FlowTestDefinition
        {
            HttpResponses = new Dictionary<string, List<FlowHttpResponseOverride>>
            {
                [stepId.ToString()] = new()
                {
                    new FlowHttpResponseOverride { StatusCode = 200, ResponseBody = "{\"page\":1}" },
                    new FlowHttpResponseOverride { StatusCode = 200, ResponseBody = "{\"page\":2}" }
                }
            },
            CrossReferenceRows = new List<FlowCrossReferenceFixture>
            {
                new() { ListName = "Orders", KeyValue = "A-1" }
            }
        };
        var context = FlowTestContext.FromJson(JsonConvert.SerializeObject(definition), Guid.NewGuid());

        Assert.True(context.ContainsCrossReferenceKey("Orders", "A-1"));
        Assert.True(context.TryTakeHttpResponse(stepId, out var first));
        Assert.True(context.TryTakeHttpResponse(stepId, out var second));
        Assert.True(context.TryTakeHttpResponse(stepId, out var repeated));
        Assert.Contains("\"page\":1", first.Response);
        Assert.Contains("\"page\":2", second.Response);
        Assert.Equal(second.Response, repeated.Response);
    }
}