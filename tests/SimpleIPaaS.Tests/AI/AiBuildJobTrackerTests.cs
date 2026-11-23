using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fluxor;
using SimpleIPaaS.Client.Services;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Tests.AI;

public sealed class AiBuildJobTrackerTests
{
    [Fact]
    public async Task RestoreActiveJobAsync_FollowsNewestActiveJobWithoutCreatingAnother()
    {
        var older = new AiBuildJobDto
        {
            Id = Guid.NewGuid(),
            Status = "Running",
            CreatedAt = DateTime.UtcNow.AddMinutes(-2),
            ToolCallsUsed = 4
        };
        var latest = new AiBuildJobDto
        {
            Id = Guid.NewGuid(),
            Status = "Queued",
            CreatedAt = DateTime.UtcNow.AddMinutes(-1),
            ProgressNote = "Waiting in the build queue"
        };
        var handler = new JobHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new[]
            {
                new AiBuildJobDto { Id = Guid.NewGuid(), Status = "Failed" },
                older,
                latest
            })
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        using var tracker = new AiBuildJobTracker(client, new StubDispatcher());

        await tracker.RestoreActiveJobAsync();

        Assert.Equal(latest.Id, tracker.CurrentJob?.Id);
        Assert.True(tracker.IsBusy);
        Assert.False(tracker.IsRestoring);
        Assert.Equal(HttpMethod.Get, Assert.Single(handler.Requests).Method);
        Assert.Equal("http://localhost/api/ai/flow/jobs", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task RestoreActiveJobAsync_IsIdempotentWhenAlreadyFollowingAJob()
    {
        var existing = new AiBuildJobDto { Id = Guid.NewGuid(), Status = "Running" };
        var handler = new JobHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(Array.Empty<AiBuildJobDto>())
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        using var tracker = new AiBuildJobTracker(client, new StubDispatcher());
        tracker.Follow(existing);

        await tracker.RestoreActiveJobAsync();

        Assert.Equal(existing.Id, tracker.CurrentJob?.Id);
        Assert.Empty(handler.Requests);
    }

    private sealed class JobHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(responder(request));
        }
    }

    private sealed class StubDispatcher : IDispatcher
    {
        public event EventHandler<ActionDispatchedEventArgs>? ActionDispatched;

        public void Dispatch(object action) => ActionDispatched?.Invoke(this, new ActionDispatchedEventArgs(action));
    }
}
