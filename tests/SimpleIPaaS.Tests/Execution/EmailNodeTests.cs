using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.Execution;

public sealed class EmailNodeTests
{
    private readonly StubIntegrationRepository _flows = new();
    private readonly StubExecutionRepository _executions = new();
    private readonly FakeEmailSender _email = new();

    private FlowExecutor CreateExecutor(IAdvancedCodeExecutionService? code = null) => new(
        _flows,
        new UnreachableTransportEngine(),
        code ?? new UnreachableCodeExecutionService(),
        _executions,
        new StubCrossReferenceRepository(),
        _email,
        NullLogger<FlowExecutor>.Instance);

    private static string Config(
        string to = "ops@example.com",
        string cc = "",
        string bcc = "",
        string replyTo = "",
        string subject = "Order synced",
        string body = "Body",
        bool isHtml = false,
        bool useScript = false,
        bool continueOnError = false) =>
        new EmailStepConfig
        {
            To = to,
            Cc = cc,
            Bcc = bcc,
            ReplyTo = replyTo,
            Subject = subject,
            Body = body,
            IsHtml = isHtml,
            UseScript = useScript,
            ContinueOnError = continueOnError
        }.Write();

    private (IntegrationFlow Flow, FlowExecution Execution, IntegrationStep Email, IntegrationStep Inspect) Seed(
        string stepConfig,
        string mappingCode = "",
        bool allowPostReplay = false,
        bool withUpstreamMapping = false)
    {
        var email = new IntegrationStep
        {
            Id = Guid.NewGuid(),
            NodeName = "Notify",
            StepType = StepType.Email,
            StepConfig = stepConfig,
            MappingCode = mappingCode
        };
        var inspect = new IntegrationStep
        {
            Id = Guid.NewGuid(),
            NodeName = "Inspect",
            StepType = StepType.Debug
        };
        var flow = new IntegrationFlow { Name = "Email flow", AllowPostReplay = allowPostReplay };
        if (withUpstreamMapping)
        {
            var mapping = new IntegrationStep
            {
                Id = Guid.NewGuid(),
                NodeName = "MapOrder",
                StepType = StepType.Mapping,
                MappingCode = "return new { };"
            };
            flow.Nodes.Add(mapping);
            flow.Edges.Add(new IntegrationEdge
            {
                SourceNodeId = mapping.Id,
                TargetNodeId = email.Id,
                SourcePortId = "right",
                TargetPortId = "left"
            });
        }

        flow.Nodes.Add(email);
        flow.Nodes.Add(inspect);
        flow.Edges.Add(new IntegrationEdge
        {
            SourceNodeId = email.Id,
            TargetNodeId = inspect.Id,
            SourcePortId = "right",
            TargetPortId = "left"
        });

        _flows.Seed(flow);
        var execution = new FlowExecution
        {
            FlowId = flow.Id,
            FlowName = flow.Name,
            TriggerSource = "Test"
        };
        _executions.Seed(execution);
        return (flow, execution, email, inspect);
    }

    private Task<FlowExecution> ExecuteAsync(
        IntegrationFlow flow,
        FlowExecution execution,
        string trigger = "{\"orderNo\":\"A-1\"}",
        FlowTestContext? testContext = null,
        IAdvancedCodeExecutionService? code = null) =>
        CreateExecutor(code).ExecuteFlowAsync(
            flow.Id, execution.Id, trigger, CancellationToken.None, testContext);

    private StepExecution FindStep(string name) =>
        _executions.StepExecutions.Single(step => step.NodeName == name);

    [Fact]
    public async Task StaticConfigSendsOneMessageWithAllHeadersAndPassesTheInputThrough()
    {
        var seed = Seed(Config(
            to: "a@example.com; b@example.com",
            cc: "c@example.com",
            bcc: "audit@example.com",
            replyTo: "support@example.com",
            subject: "Order synced",
            body: "Line 1\nLine 2"));

        var result = await ExecuteAsync(seed.Flow, seed.Execution);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        var message = Assert.Single(_email.Messages);
        Assert.Equal("a@example.com, b@example.com", message.To);
        Assert.Equal(new[] { "c@example.com" }, message.Cc);
        Assert.Equal(new[] { "audit@example.com" }, message.Bcc);
        Assert.Equal("support@example.com", message.ReplyTo);
        Assert.Equal("Order synced", message.Subject);
        Assert.Contains("Line 1<br>Line 2", message.HtmlBody);
        Assert.Equal("Line 1\nLine 2", message.TextBody);

        var emailStep = FindStep("Notify");
        Assert.Equal(ExecutionStatus.Success, emailStep.Status);
        var summary = JObject.Parse(emailStep.ResponsePayload!);
        Assert.Equal(2, summary["sentTo"]!.Count());
        Assert.Single(summary["cc"]!);
        Assert.Equal(1, summary["bcc"]!.Value<int>());
        Assert.Equal("support@example.com", summary["replyTo"]!.ToString());
        Assert.Equal("Order synced", summary["subject"]!.ToString());
        Assert.Null(summary["body"]);

        var inspectState = (JObject)JObject.Parse(FindStep("Inspect").ResponsePayload!)["flowState"]!;
        Assert.Equal("A-1", inspectState["Notify"]!["orderNo"]!.ToString());
    }

    [Fact]
    public async Task ScriptValuesOverrideStaticValuesAndMissingFieldsFallBack()
    {
        var seed = Seed(
            Config(
                to: "static@example.com",
                subject: "Static subject",
                body: "Static body",
                useScript: true),
            mappingCode: "return new { to = \"script@example.com\", subject = \"Script subject\" };");
        var code = new EmailScriptService
        {
            EmailJson = "{\"to\":\"script@example.com\",\"subject\":\"Script subject\"}"
        };

        await ExecuteAsync(seed.Flow, seed.Execution, code: code);

        var message = Assert.Single(_email.Messages);
        Assert.Equal("script@example.com", message.To);
        Assert.Equal("Script subject", message.Subject);
        Assert.Equal("Static body", message.TextBody);
    }

    [Fact]
    public async Task PlainTextBodyIsHtmlEncodedAndHtmlBodyIsSentAsIs()
    {
        var plain = Seed(Config(body: "Hello\n<world>"));
        await ExecuteAsync(plain.Flow, plain.Execution);

        var plainMessage = Assert.Single(_email.Messages);
        Assert.Contains("Hello<br>&lt;world&gt;", plainMessage.HtmlBody);
        Assert.Equal("Hello\n<world>", plainMessage.TextBody);

        var html = Seed(Config(body: "<b>Hello</b>", isHtml: true));
        await ExecuteAsync(html.Flow, html.Execution);

        var htmlMessage = _email.Messages[^1];
        Assert.Equal("<b>Hello</b>", htmlMessage.HtmlBody);
        Assert.Equal("Hello", htmlMessage.TextBody);
    }

    [Fact]
    public async Task NoRecipientsFailsAndCreatesADeadLetter()
    {
        var seed = Seed(Config(to: "", cc: "", subject: "Order synced"));

        var result = await ExecuteAsync(seed.Flow, seed.Execution);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Empty(_email.Messages);
        Assert.Single(_executions.DeadLetters);
        Assert.Contains("no To recipients", _executions.DeadLetters[0].ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvalidAddressFailsAndCreatesADeadLetter()
    {
        var seed = Seed(Config(to: "not-an-email"));

        var result = await ExecuteAsync(seed.Flow, seed.Execution);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Empty(_email.Messages);
        Assert.Single(_executions.DeadLetters);
        Assert.Contains("invalid To address", _executions.DeadLetters[0].ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MoreThanTwentyRecipientsFailsAndCreatesADeadLetter()
    {
        var recipients = string.Join(";", Enumerable.Range(0, 21).Select(index => $"user{index}@example.com"));
        var seed = Seed(Config(to: recipients));

        var result = await ExecuteAsync(seed.Flow, seed.Execution);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Empty(_email.Messages);
        Assert.Single(_executions.DeadLetters);
        Assert.Contains("maximum is 20", _executions.DeadLetters[0].ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SenderFailureFailsTheStepAndCreatesADeadLetter()
    {
        _email.ThrowOnSend = new InvalidOperationException("smtp down");
        var seed = Seed(Config());

        var result = await ExecuteAsync(seed.Flow, seed.Execution);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(ExecutionStatus.Failed, FindStep("Notify").Status);
        Assert.Single(_executions.DeadLetters);
        Assert.DoesNotContain(_executions.StepExecutions, step => step.NodeName == "Inspect");
    }

    [Fact]
    public async Task SenderFailureWithContinueOnErrorSucceedsAndDownstreamRuns()
    {
        _email.ThrowOnSend = new InvalidOperationException("smtp down");
        var seed = Seed(Config(continueOnError: true));

        var result = await ExecuteAsync(seed.Flow, seed.Execution);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        var emailStep = FindStep("Notify");
        Assert.Equal(ExecutionStatus.Success, emailStep.Status);
        Assert.Contains("smtp down", emailStep.ResponsePayload);
        Assert.Contains(_executions.StepExecutions, step => step.NodeName == "Inspect");
        Assert.Empty(_executions.DeadLetters);
    }

    [Fact]
    public async Task TestRunReturnsTheResolvedPreviewWithoutSending()
    {
        var seed = Seed(Config(
            to: "a@example.com",
            cc: "c@example.com",
            subject: "Preview subject",
            body: "Preview body"));
        var testContext = FlowTestContext.FromJson("{}", seed.Execution.Id);

        var result = await ExecuteAsync(seed.Flow, seed.Execution, testContext: testContext);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Empty(_email.Messages);
        var preview = JObject.Parse(FindStep("Notify").ResponsePayload!);
        Assert.Equal("a@example.com", preview["to"]![0]!.ToString());
        Assert.Equal("c@example.com", preview["cc"]![0]!.ToString());
        Assert.Equal("Preview subject", preview["subject"]!.ToString());
        Assert.Equal("Preview body", preview["body"]!.ToString());
    }

    [Fact]
    public async Task SubjectPlaceholderResolvesFromUpstreamNode()
    {
        var seed = Seed(
            Config(
                to: "{{flowState.MapOrder.customerEmail}}",
                subject: "Order {{flowState.MapOrder.orderNo}} synced"),
            withUpstreamMapping: true);
        var code = new EmailScriptService
        {
            MappingJson = "{\"orderNo\":\"LIVE-1001\",\"customerEmail\":\"cust@example.com\"}"
        };

        await ExecuteAsync(seed.Flow, seed.Execution, code: code);

        var message = Assert.Single(_email.Messages);
        Assert.Equal("cust@example.com", message.To);
        Assert.Equal("Order LIVE-1001 synced", message.Subject);
    }

    [Fact]
    public async Task PlaceholderInToThatResolvesEmptyFailsAndCreatesADeadLetter()
    {
        var seed = Seed(
            Config(to: "{{flowState.MapOrder.missingEmail}}"),
            withUpstreamMapping: true);
        var code = new EmailScriptService { MappingJson = "{\"orderNo\":\"A-1\"}" };

        var result = await ExecuteAsync(seed.Flow, seed.Execution, code: code);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Empty(_email.Messages);
        Assert.Single(_executions.DeadLetters);
        Assert.Contains("no To recipients", _executions.DeadLetters[0].ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InjectedCrLfInSubjectFailsAndCreatesADeadLetter()
    {
        var seed = Seed(
            Config(subject: "Order {{flowState.MapOrder.orderNo}}"),
            withUpstreamMapping: true);
        var code = new EmailScriptService
        {
            MappingJson = "{\"orderNo\":\"A-1\\r\\nBcc: evil@example.com\"}"
        };

        var result = await ExecuteAsync(seed.Flow, seed.Execution, code: code);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Empty(_email.Messages);
        Assert.Single(_executions.DeadLetters);
        Assert.Contains("line break", _executions.DeadLetters[0].ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HtmlBodyEncodesInsertedValuesButLeavesTemplateHtmlAlone()
    {
        var seed = Seed(
            Config(
                to: "a@example.com",
                subject: "Alert",
                body: "<p>Hello {{flowState.MapOrder.customerName}}</p>",
                isHtml: true),
            withUpstreamMapping: true);
        var code = new EmailScriptService
        {
            MappingJson = "{\"customerName\":\"<script>alert(1)</script>\"}"
        };

        await ExecuteAsync(seed.Flow, seed.Execution, code: code);

        var message = Assert.Single(_email.Messages);
        Assert.Contains("<p>Hello &lt;script&gt;alert(1)&lt;/script&gt;</p>", message.HtmlBody);
    }

    [Fact]
    public async Task MissingPlaceholderIsSentAndRecorded()
    {
        var seed = Seed(
            Config(subject: "Order {{flowState.MapOrder.nope}} synced"),
            withUpstreamMapping: true);
        var code = new EmailScriptService { MappingJson = "{\"orderNo\":\"A-1\"}" };

        await ExecuteAsync(seed.Flow, seed.Execution, code: code);

        var message = Assert.Single(_email.Messages);
        Assert.Equal("Order  synced", message.Subject);
        var summary = JObject.Parse(FindStep("Notify").ResponsePayload!);
        Assert.Equal("flowState.MapOrder.nope", summary["missingPlaceholders"]![0]!.ToString());
    }

    [Fact]
    public async Task ScriptValuesAreNotRenderedAgain()
    {
        var seed = Seed(Config(useScript: true), mappingCode: "return new { };");
        var code = new EmailScriptService
        {
            EmailJson = "{\"to\":\"a@example.com\",\"subject\":\"{{not.a.placeholder}}\",\"body\":\"Hi\"}"
        };

        await ExecuteAsync(seed.Flow, seed.Execution, code: code);

        var message = Assert.Single(_email.Messages);
        Assert.Equal("{{not.a.placeholder}}", message.Subject);
        Assert.Null(JObject.Parse(FindStep("Notify").ResponsePayload!)["missingPlaceholders"]);
    }

    private sealed class EmailScriptService : IAdvancedCodeExecutionService
    {
        public string EmailJson { get; set; } = "{}";
        public string MappingJson { get; set; } = "{}";

        public Task<string> ExecuteMappingAsync(string csharpCode, string flowStateJson, string persistedStateJson, string? helperCode = null) =>
            Task.FromResult(MappingJson);

        public Task<string> ExecuteEmailAsync(string csharpCode, string flowStateJson, string persistedStateJson, string? helperCode = null) =>
            Task.FromResult(EmailJson);

        public Task<string> ExecutePostFlightAsync(string csharpCode, string flowStateJson, string persistedStateJson, string httpResponseJson, string? helperCode = null) =>
            Task.FromResult("{}");

        public Task<string> ExecuteUrlAsync(string csharpCode, string flowStateJson, string persistedStateJson, string? helperCode = null) =>
            Task.FromResult("{}");

        public Task<bool> ExecuteBranchAsync(string csharpCode, string flowStateJson, string persistedStateJson, string? helperCode = null) =>
            Task.FromResult(false);

        public Task ValidateAsync(string csharpCode, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
