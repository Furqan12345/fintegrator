using System.Linq;
using SimpleIPaaS.Api.Validation;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Tests.AI;

public sealed class StepConfigLinterTests
{
    private static IntegrationFlowDto Flow(params IntegrationStepDto[] nodes) => new()
    {
        Name = "Test flow",
        Nodes = nodes.ToList(),
        Edges = new()
    };

    [Fact]
    public void ValidScheduleAndStoreDraftHasNoErrors()
    {
        var (errors, _) = StepConfigLinter.Lint(Flow(
            new IntegrationStepDto
            {
                NodeName = "Trigger",
                StepType = "Schedule",
                StepConfig = "{\"schedule\":{\"mode\":\"Cron\",\"cronExpression\":\"0 9 * * *\"}}"
            },
            new IntegrationStepDto
            {
                NodeName = "Store orders",
                StepType = "CrossReferenceStore",
                StepConfig = "{\"listName\":\"shopify-orders\",\"arrayPath\":\"flowState.Fetch.orders\",\"keyPaths\":[\"id\"]}"
            }));

        Assert.Empty(errors);
    }

    [Fact]
    public void CrossReferenceStoreRequiresListNameAndArrayPath()
    {
        var (errors, _) = StepConfigLinter.Lint(Flow(
            new IntegrationStepDto
            {
                NodeName = "Store orders",
                StepType = "CrossReferenceStore",
                StepConfig = "{\"keyPaths\":[\"id\"]}"
            }));

        Assert.Contains(errors, error => error.Contains("listName", System.StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("arrayPath", System.StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidStepConfigJsonIsAnError()
    {
        var (errors, _) = StepConfigLinter.Lint(Flow(
            new IntegrationStepDto
            {
                NodeName = "Store orders",
                StepType = "CrossReferenceStore",
                StepConfig = "{not json"
            }));

        Assert.Contains(errors, error => error.Contains("not valid JSON", System.StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownStepConfigKeysAreWarningsNotErrors()
    {
        var (errors, warnings) = StepConfigLinter.Lint(Flow(
            new IntegrationStepDto
            {
                NodeName = "Store orders",
                StepType = "CrossReferenceStore",
                StepConfig = "{\"listName\":\"orders\",\"arrayPath\":\"orders\",\"keyPaths\":[\"id\"],\"madeUpField\":1}"
            }));

        Assert.Empty(errors);
        Assert.Contains(warnings, warning => warning.Contains("madeUpField", System.StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("CrossReferenceFilter", "{\"listName\":\"orders\",\"arrayPath\":\"orders\",\"keyPaths\":[\"id\"],\"filterMode\":\"Merge\"}", "filterMode")]
    [InlineData("Schedule", "{\"schedule\":{\"mode\":\"Weekly\",\"cronExpression\":\"0 9 * * *\"}}", "schedule.mode")]
    [InlineData("FanOut", "{\"fanOut\":{\"itemVariable\":\"item\",\"maxItems\":10}}", "arrayPath")]
    public void InvalidEnumValuesAndMissingFieldsAreErrors(string stepType, string stepConfig, string expected)
    {
        var (errors, _) = StepConfigLinter.Lint(Flow(
            new IntegrationStepDto { NodeName = "Node", StepType = stepType, StepConfig = stepConfig }));

        if (expected == "arrayPath")
            Assert.Contains(errors, error => error.Contains("fanOut.arrayPath", System.StringComparison.Ordinal));
        else
            Assert.Contains(errors, error => error.Contains(expected, System.StringComparison.Ordinal));
    }

    [Fact]
    public void EmailStaticModeRequiresToAndSubject()
    {
        var (errors, _) = StepConfigLinter.Lint(Flow(
            new IntegrationStepDto
            {
                NodeName = "Notify",
                StepType = "Email",
                StepConfig = "{\"useScript\":false}"
            }));

        Assert.Contains(errors, error => error.Contains("stepConfig.to", System.StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("stepConfig.subject", System.StringComparison.Ordinal));
    }

    [Fact]
    public void EmailScriptModeRequiresMappingCode()
    {
        var (errors, _) = StepConfigLinter.Lint(Flow(
            new IntegrationStepDto
            {
                NodeName = "Notify",
                StepType = "Email",
                StepConfig = "{\"useScript\":true}"
            }));

        Assert.Contains(errors, error => error.Contains("mappingCode", System.StringComparison.Ordinal));
    }

    [Fact]
    public void EmailInvalidRecipientIsAWarning()
    {
        var (errors, warnings) = StepConfigLinter.Lint(Flow(
            new IntegrationStepDto
            {
                NodeName = "Notify",
                StepType = "Email",
                StepConfig = "{\"to\":\"not-an-email\",\"subject\":\"Order synced\"}"
            }));

        Assert.Empty(errors);
        Assert.Contains(warnings, warning => warning.Contains("valid email address", System.StringComparison.Ordinal));
    }

    [Fact]
    public void EmailUnknownPlaceholderNodeIsAWarning()
    {
        var (errors, warnings) = StepConfigLinter.Lint(Flow(
            new IntegrationStepDto
            {
                NodeName = "Notify",
                StepType = "Email",
                StepConfig = "{\"to\":\"ops@example.com\",\"subject\":\"Order {{flowState.Missing.orderNo}} synced\"}"
            }));

        Assert.Empty(errors);
        Assert.Contains(warnings, warning => warning.Contains("unknown node 'Missing'", System.StringComparison.Ordinal));
    }

    [Fact]
    public void EmailUnbalancedPlaceholderIsAnError()
    {
        var (errors, _) = StepConfigLinter.Lint(Flow(
            new IntegrationStepDto
            {
                NodeName = "Notify",
                StepType = "Email",
                StepConfig = "{\"to\":\"ops@example.com\",\"subject\":\"Order {{flowState.MapOrder.orderNo\"}"
            }));

        Assert.Contains(errors, error => error.Contains("unbalanced", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EmailPlaceholderWithBadPrefixIsAnError()
    {
        var (errors, _) = StepConfigLinter.Lint(Flow(
            new IntegrationStepDto
            {
                NodeName = "Notify",
                StepType = "Email",
                StepConfig = "{\"to\":\"ops@example.com\",\"subject\":\"Order {{trigger.orderNo}}\"}"
            }));

        Assert.Contains(errors, error => error.Contains("flowState. or persistedState", System.StringComparison.Ordinal));
    }

    [Fact]
    public void EmailBccAndReplyToAreKnownKeys()
    {
        var (errors, warnings) = StepConfigLinter.Lint(Flow(
            new IntegrationStepDto
            {
                NodeName = "Notify",
                StepType = "Email",
                StepConfig = "{\"to\":\"ops@example.com\",\"bcc\":\"audit@example.com\",\"replyTo\":\"support@example.com\",\"subject\":\"Order synced\"}"
            }));

        Assert.Empty(errors);
        Assert.DoesNotContain(warnings, warning => warning.Contains("bcc", System.StringComparison.OrdinalIgnoreCase)
            || warning.Contains("replyTo", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HttpActionRequiresUrlAndValidMethod()
    {
        var (errors, _) = StepConfigLinter.Lint(Flow(
            new IntegrationStepDto { NodeName = "Call API", StepType = "HttpAction", HttpMethod = "TELEPORT" }));

        Assert.Contains(errors, error => error.Contains("httpMethod", System.StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("endpointUrl", System.StringComparison.Ordinal));
    }

    [Fact]
    public void HttpActionConnectionModeRequiresConnectionId()
    {
        var (errors, _) = StepConfigLinter.Lint(Flow(
            new IntegrationStepDto { NodeName = "Call API", StepType = "HttpAction", UrlMode = "Connection" }));

        Assert.Contains(errors, error => error.Contains("connectionId", System.StringComparison.Ordinal));
    }

    [Fact]
    public void CronScheduleRequiresFiveFieldExpression()
    {
        var (errors, _) = StepConfigLinter.Lint(Flow(
            new IntegrationStepDto
            {
                NodeName = "Trigger",
                StepType = "Schedule",
                StepConfig = "{\"schedule\":{\"mode\":\"Cron\",\"cronExpression\":\"every day\"}}"
            }));

        Assert.Contains(errors, error => error.Contains("5 fields", System.StringComparison.Ordinal));
    }

    [Fact]
    public void ApiPacketHeadersMustBeJsonStrings()
    {
        var (errors, _) = StepConfigLinter.Lint(Flow(
            new IntegrationStepDto
            {
                NodeName = "Call API",
                StepType = "HttpAction",
                EndpointUrl = "https://api.example.test/orders",
                StepConfig = "{\"apiPacket\":{\"headersJson\":\"not-json\"}}"
            }));

        Assert.Contains(errors, error => error.Contains("headersJson", System.StringComparison.Ordinal));
    }

    [Fact]
    public void PaginationStyleRequiresItsContinuationFields()
    {
        var (errors, _) = StepConfigLinter.Lint(Flow(
            new IntegrationStepDto
            {
                NodeName = "Fetch orders",
                StepType = "HttpAction",
                EndpointUrl = "https://api.example.test/orders",
                StepConfig = "{\"pagination\":{\"style\":\"Cursor\",\"maxPages\":5}}"
            }));

        Assert.Contains(errors, error => error.Contains("cursorResponsePath", System.StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("cursorRequestParameter", System.StringComparison.Ordinal));
    }

    [Fact]
    public void AmazonNextTokenRequiresBothTokenFields()
    {
        var (errors, _) = StepConfigLinter.Lint(Flow(
            new IntegrationStepDto
            {
                NodeName = "Fetch orders",
                StepType = "HttpAction",
                EndpointUrl = "https://api.example.test/orders",
                StepConfig = "{\"pagination\":{\"style\":\"AmazonNextToken\",\"maxPages\":5}}"
            }));

        Assert.Contains(errors, error => error.Contains("nextTokenPath", System.StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("nextTokenParameter", System.StringComparison.Ordinal));
    }

    [Fact]
    public void HardcodedCredentialsInStepConfigAreWarnings()
    {
        var (errors, warnings) = StepConfigLinter.Lint(Flow(
            new IntegrationStepDto
            {
                NodeName = "Call API",
                StepType = "HttpAction",
                EndpointUrl = "https://api.example.test/orders",
                StepConfig = "{\"apiPacket\":{\"headersJson\":\"{\\\"X-Shopify-Access-Token\\\":\\\"shpat_abc123\\\"}\"}}"
            }));

        Assert.Empty(errors);
        Assert.Contains(warnings, warning => warning.Contains("hardcode a credential", System.StringComparison.Ordinal));
    }
}
