using SimpleIPaaS.Api.Controllers;

namespace SimpleIPaaS.Tests.Scheduling;

public class CronPreviewTests
{
    [Theory]
    [InlineData("*/5 * * * *")]
    [InlineData("0 9 * * 1-5")]
    [InlineData("0 0 1 1 *")]
    public void TryBuildCronPreview_AcceptsFiveFieldExpressions(string expression)
    {
        Assert.True(IntegrationFlowController.TryBuildCronPreview(expression, 3, out var preview, out var error));
        Assert.Empty(error);
        Assert.Equal(3, preview.NextOccurrences.Count);
        Assert.All(preview.NextOccurrences, occurrence => Assert.True(occurrence > DateTime.UtcNow));
    }

    [Theory]
    [InlineData("*/30 * * * * *")]
    [InlineData("0 0 9 * * 1-5")]
    [InlineData("15 30 2 * * *")]
    public void TryBuildCronPreview_AcceptsSixFieldExpressions(string expression)
    {
        Assert.True(IntegrationFlowController.TryBuildCronPreview(expression, 3, out var preview, out var error));
        Assert.Empty(error);
        Assert.Equal(3, preview.NextOccurrences.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a cron expression")]
    [InlineData("* * *")]
    [InlineData("*/5 * * *")]
    [InlineData("99 * * * *")]
    [InlineData("* * * * * * *")]
    public void TryBuildCronPreview_RejectsInvalidExpressions(string expression)
    {
        Assert.False(IntegrationFlowController.TryBuildCronPreview(expression, 3, out var preview, out var error));
        Assert.NotEmpty(error);
        Assert.Empty(preview.NextOccurrences);
    }

    [Fact]
    public void TryBuildCronPreview_ReturnsOccurrencesInAscendingOrder()
    {
        Assert.True(IntegrationFlowController.TryBuildCronPreview("*/5 * * * *", 5, out var preview, out _));

        Assert.Equal(5, preview.NextOccurrences.Count);
        Assert.Equal(preview.NextOccurrences.OrderBy(occurrence => occurrence).ToList(), preview.NextOccurrences);
    }

    [Fact]
    public void TryBuildCronPreview_ClampsRequestedCount()
    {
        Assert.True(IntegrationFlowController.TryBuildCronPreview("* * * * *", 0, out var defaulted, out _));
        Assert.Equal(3, defaulted.NextOccurrences.Count);

        Assert.True(IntegrationFlowController.TryBuildCronPreview("* * * * *", 500, out var clamped, out _));
        Assert.Equal(10, clamped.NextOccurrences.Count);
    }

    [Fact]
    public void TryBuildCronPreview_DescribesBothFieldCounts()
    {
        Assert.True(IntegrationFlowController.TryBuildCronPreview("*/5 * * * *", 1, out var fiveField, out _));
        Assert.Contains("every 5 minutes", fiveField.Description);

        Assert.True(IntegrationFlowController.TryBuildCronPreview("*/30 * * * * *", 1, out var sixField, out _));
        Assert.Contains("every 30 seconds", sixField.Description);
    }
}
