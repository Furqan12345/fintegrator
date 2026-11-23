using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.Execution;

public sealed class FailureAlertTests
{

    private readonly StubExecutionRepository _repository = new();
    private readonly FakeEmailSender _email = new();
    private readonly FailureAlertEmailBuilder _builder = new();
    private readonly Guid _flowId = Guid.NewGuid();
    private readonly Guid _executionId = Guid.NewGuid();

    public FailureAlertTests()
    {
        _repository.Seed(new FlowExecution
        {
            Id = _executionId,
            FlowId = _flowId,
            FlowName = "Order Sync",
            Status = ExecutionStatus.Failed
        });
    }

    private readonly IConfiguration _configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["App:PublicBaseUrl"] = "http://localhost:5001"
        })
        .Build();

    private FailureAlertService CreateService() => new(
        _repository,
        _email,
        _builder,
        NullLogger<FailureAlertService>.Instance,
        _configuration);

    private static AlertSettings Settings(
        DateTime enabledAt,
        string recipients = "ops@example.com;alerts@example.com",
        int cooldownMinutes = 15) => new()
    {
        Enabled = true,
        Recipients = recipients,
        CooldownMinutes = cooldownMinutes,
        EnabledAt = enabledAt
    };

    private DeadLetterEntry Seed(
        string flowName,
        string nodeName,
        string error,
        DateTime createdAt,
        string payload = "{}")
    {
        var entry = new DeadLetterEntry
        {
            Id = Guid.NewGuid(),
            FlowExecutionId = _executionId,
            FlowName = flowName,
            NodeName = nodeName,
            ErrorMessage = error,
            Payload = payload,
            CreatedAt = createdAt,
            Status = "Pending"
        };
        _repository.DeadLetters.Add(entry);
        return entry;
    }

    [Fact]
    public async Task ThreeFailuresSendOneEmailPerRecipientAndMarkAlerted()
    {
        var now = new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);
        var settings = Settings(now.AddHours(-1));
        var entries = new[]
        {
            Seed("Order Sync", "Push", "first", now.AddMinutes(-30)),
            Seed("Order Sync", "Push", "second", now.AddMinutes(-20)),
            Seed("Order Sync", "Push", "third", now.AddMinutes(-10))
        };

        var alerted = await CreateService().ProcessAsync(settings, now);

        Assert.Equal(3, alerted);
        Assert.Equal(2, _email.Messages.Count);
        Assert.All(_email.Messages, message => Assert.Contains("3 failed orders", message.Subject));
        Assert.All(entries, entry => Assert.Equal(now, entry.AlertedAt));
        Assert.Equal(now, settings.LastSentAt);
    }

    [Fact]
    public async Task CooldownBlocksSecondEmailAndSendsAfterTheWindow()
    {
        var now = new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);
        var settings = Settings(now.AddHours(-2), cooldownMinutes: 15);
        settings.LastSentAt = now.AddMinutes(-10);
        Seed("Order Sync", "Push", "first", now.AddMinutes(-5));

        var service = CreateService();
        Assert.Equal(0, await service.ProcessAsync(settings, now));
        Assert.Empty(_email.Messages);

        var afterCooldown = now.AddMinutes(20);
        Assert.Equal(1, await service.ProcessAsync(settings, afterCooldown));
        Assert.Equal(2, _email.Messages.Count);
        Assert.Equal(afterCooldown, settings.LastSentAt);
    }

    [Fact]
    public async Task EntriesBeforeEnabledAtAreIgnored()
    {
        var now = new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);
        var settings = Settings(now.AddHours(-1));
        var oldEntry = Seed("Order Sync", "Push", "old", now.AddHours(-2));
        var freshEntry = Seed("Order Sync", "Push", "fresh", now.AddMinutes(-10));

        var alerted = await CreateService().ProcessAsync(settings, now);

        Assert.Equal(1, alerted);
        Assert.Null(oldEntry.AlertedAt);
        Assert.Equal(now, freshEntry.AlertedAt);
    }

    [Fact]
    public async Task DisabledOrEmptyRecipientsSendNothing()
    {
        var now = new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);
        var entry = Seed("Order Sync", "Push", "boom", now.AddMinutes(-5));

        var disabled = Settings(now.AddHours(-1));
        disabled.Enabled = false;
        Assert.Equal(0, await CreateService().ProcessAsync(disabled, now));

        var noRecipients = Settings(now.AddHours(-1), recipients: "");
        Assert.Equal(0, await CreateService().ProcessAsync(noRecipients, now));

        Assert.Empty(_email.Messages);
        Assert.Null(entry.AlertedAt);
    }

    [Fact]
    public async Task WhenTheSenderThrowsNothingIsMarkedAndTheNextRunRetries()
    {
        var now = new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);
        var settings = Settings(now.AddHours(-1));
        var entries = new[]
        {
            Seed("Order Sync", "Push", "first", now.AddMinutes(-5)),
            Seed("Order Sync", "Push", "second", now.AddMinutes(-4))
        };

        _email.ThrowOnSend = new InvalidOperationException("smtp down");
        var service = CreateService();
        Assert.Equal(0, await service.ProcessAsync(settings, now));
        Assert.All(entries, entry => Assert.Null(entry.AlertedAt));
        Assert.Null(settings.LastSentAt);

        _email.ThrowOnSend = null;
        Assert.Equal(2, await service.ProcessAsync(settings, now));
        Assert.All(entries, entry => Assert.Equal(now, entry.AlertedAt));
    }

    [Fact]
    public async Task BodyIsHtmlEncodedContainsDeepLinkAndNoPayload()
    {
        var now = new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);
        var settings = Settings(now.AddHours(-1));
        var entry = Seed(
            "Order <Sync>",
            "Push & Pull",
            "<script>alert('x')</script>",
            now.AddMinutes(-5),
            payload: "SECRET-PAYLOAD");

        await CreateService().ProcessAsync(settings, now);

        Assert.Equal(2, _email.Messages.Count);
        foreach (var message in _email.Messages)
        {
            Assert.Contains("&lt;script&gt;", message.HtmlBody);
            Assert.DoesNotContain("<script>", message.HtmlBody);
            Assert.Contains("/deadletters?id=" + entry.Id, message.HtmlBody);
            Assert.DoesNotContain("SECRET-PAYLOAD", message.HtmlBody);
            Assert.DoesNotContain("SECRET-PAYLOAD", message.TextBody);
        }
    }

    [Fact]
    public async Task TwentyFiveEntriesListTwentyAndFiveMore()
    {
        var now = new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);
        var settings = Settings(now.AddHours(-1));
        for (var index = 0; index < 25; index++)
        {
            Seed("Order Sync", "Push", "error " + index, now.AddMinutes(-30).AddSeconds(index));
        }

        await CreateService().ProcessAsync(settings, now);

        var message = _email.Messages[0];
        Assert.Contains("20", message.HtmlBody);
        Assert.Contains("and 5 more", message.TextBody);
        Assert.Contains("/deadletters?flowId=", message.HtmlBody);
    }

    [Fact]
    public void MoreLink_FallsBackToTheUnscopedQueueForMixedFlows()
    {
        var flowA = Guid.NewGuid();
        var flowB = Guid.NewGuid();
        var candidates = Enumerable.Range(0, 21)
            .Select(index => Candidate(index < 11 ? flowA : flowB, Guid.NewGuid(), index))
            .ToList();

        var email = _builder.Build(candidates, "http://localhost:5001");

        Assert.Contains("href=\"http://localhost:5001/deadletters\"", email.HtmlBody);
        Assert.DoesNotContain("/deadletters?flowId=", email.HtmlBody);
    }

    [Fact]
    public void MoreLink_UsesFlowOnlyWhenSeveralStepsShareOneFlow()
    {
        var flowId = Guid.NewGuid();
        var stepA = Guid.NewGuid();
        var stepB = Guid.NewGuid();
        var candidates = Enumerable.Range(0, 21)
            .Select(index => Candidate(flowId, index % 2 == 0 ? stepA : stepB, index))
            .ToList();

        var email = _builder.Build(candidates, "http://localhost:5001");

        Assert.Contains($"href=\"http://localhost:5001/deadletters?flowId={flowId}\"", email.HtmlBody);
        Assert.DoesNotContain("stepId=", email.HtmlBody);
    }

    [Fact]
    public void MoreLink_UsesFlowAndStepWhenEverythingSharesBoth()
    {
        var flowId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var candidates = Enumerable.Range(0, 21)
            .Select(index => Candidate(flowId, stepId, index))
            .ToList();

        var email = _builder.Build(candidates, "http://localhost:5001");

        Assert.Contains(
            $"http://localhost:5001/deadletters?flowId={flowId}&stepId={stepId}",
            email.TextBody);
        Assert.Contains(
            $"http://localhost:5001/deadletters?flowId={flowId}&amp;stepId={stepId}",
            email.HtmlBody);
    }

    private static (DeadLetterEntry Entry, Guid? FlowId) Candidate(Guid flowId, Guid stepId, int index) =>
        (new DeadLetterEntry
        {
            Id = Guid.NewGuid(),
            FlowName = "Order Sync",
            NodeName = "Push",
            ErrorMessage = "boom",
            Status = "Pending",
            StepId = stepId,
            CreatedAt = new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc).AddSeconds(index)
        }, flowId);
}



