using SimpleIPaaS.Api.Services;

namespace SimpleIPaaS.Tests.AI;

public sealed class ChatIntentGateTests
{
    [Theory]
    [InlineData("how are you")]
    [InlineData("How are you?")]
    [InlineData("hi")]
    [InlineData("Hi!")]
    [InlineData("  thanks.  ")]
    [InlineData("who are you")]
    [InlineData("what can you do")]
    [InlineData("good morning")]
    public void IsCasual_MatchesUnambiguousSmallTalk(string text)
    {
        Assert.True(ChatIntentGate.IsCasual(text));
    }

    [Theory]
    [InlineData("build a flow that syncs Shopify orders to Salesforce")]
    [InlineData("how are you? also add an error branch to the flow")]
    [InlineData("add a debug node at the end")]
    [InlineData("fix the pagination on the Amazon step")]
    [InlineData("Why did my last run fail?")]
    [InlineData("")]
    [InlineData(null)]
    public void IsCasual_DoesNotMatchBuildOrQuestionRequests(string? text)
    {
        Assert.False(ChatIntentGate.IsCasual(text));
    }
}
