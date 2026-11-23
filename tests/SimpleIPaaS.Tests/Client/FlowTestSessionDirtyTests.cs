using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SimpleIPaaS.Client.Components.FlowTests;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Tests.Client;

// Opening test mode on a flow with no scenarios starts a blank one. That must not count as an unsaved edit,
// otherwise leaving the page asks "Discard unsaved changes?" although nothing was touched.
public sealed class FlowTestSessionDirtyTests
{
    private sealed class EmptyScenariosHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri!.AbsolutePath.EndsWith("test-cases") ? "[]" : "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private static FlowTestSession NewSession() => new(
        new HttpClient(new EmptyScenariosHandler()) { BaseAddress = new Uri("http://localhost/") },
        new IntegrationFlowDto { Id = Guid.NewGuid(), Name = "Flow" });

    [Fact]
    public async Task OpeningTestModeOnAFlowWithoutScenarios_IsNotDirty_ButTheBlankScenarioIsReady()
    {
        var session = NewSession();
        await session.InitializeAsync();

        Assert.False(session.IsDirty);
        Assert.Equal(Guid.Empty, session.SelectedId);
        Assert.Single(session.Definition.Assertions);
    }

    [Fact]
    public async Task AnEditAfterwards_MakesItDirty()
    {
        var session = NewSession();
        await session.InitializeAsync();

        session.AddVariable();
        Assert.True(session.IsDirty);
    }

    [Fact]
    public async Task ChoosingNewScenarioExplicitly_IsDirty()
    {
        var session = NewSession();
        await session.InitializeAsync();
        Assert.False(session.IsDirty);

        session.NewScenario();
        Assert.True(session.IsDirty);
    }
}
