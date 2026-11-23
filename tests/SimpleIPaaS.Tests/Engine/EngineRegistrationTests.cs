using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Engine;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.Engine;

// Regression guard for the bug where the Engine host built its own service list and forgot
// FlowRunService — every cron tick and dead-letter replay then died with "No service for type
// 'SimpleIPaaS.Application.Services.FlowRunService' has been registered". The test builds the
// container through the same extension method the host uses (EngineServiceRegistration), so a
// registration removed from the Engine fails here instead of silently breaking a worker.
public sealed class EngineRegistrationTests
{
    private static ServiceProvider BuildEngineProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["ConnectionStrings:DefaultConnection"] = "Data Source=EngineRegistrationTest;Mode=Memory;Cache=Shared;Pooling=False",
                ["Encryption:Key"] = "0123456789abcdef0123456789abcdef"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(new StubHostEnvironment(Environments.Development));
        services.AddEngineServices(configuration, new StubHostEnvironment(Environments.Development));
        return services.BuildServiceProvider();
    }

    [Fact]
    public void EngineServices_ResolveFlowRunService_ForCronSchedulerAndDeadLetterReplay()
    {
        using var provider = BuildEngineProvider();
        using var scope = provider.CreateScope();

        // CronTriggerScheduler resolves this on every tick to enqueue a Queued execution.
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<FlowRunService>());

        // The replay path resolves the dead-letter service and its executor.
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<DeadLetterService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<FlowExecutor>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ICronScheduleRepository>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IIntegrationRepository>());

        // Failure alerts resolve the shared email sender and the host-wide digest service.
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IEmailSender>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<FailureAlertService>());
    }

    [Fact]
    public void ApiAndEngineRegistrations_AgreeOnFlowRunService()
    {
        // The API registers FlowRunService through its own extension method. Both hosts must
        // keep the registration, because both resolve it (API: /run, test runs; Engine: cron
        // and dead-letter replays).
        var apiConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["ConnectionStrings:DefaultConnection"] = "Data Source=ApiRegistrationTest;Mode=Memory;Cache=Shared;Pooling=False",
                ["Encryption:Key"] = "0123456789abcdef0123456789abcdef"
            })
            .Build();

        var apiServices = new ServiceCollection();
        apiServices.AddLogging();
        apiServices.AddSingleton<IConfiguration>(apiConfiguration);
        apiServices.AddSingleton<IHostEnvironment>(new StubHostEnvironment(Environments.Development));
        SimpleIPaaS.Api.ApiServiceRegistration.AddApiPlatformServices(apiServices, apiConfiguration);
        using var apiProvider = apiServices.BuildServiceProvider();
        using var apiScope = apiProvider.CreateScope();
        Assert.NotNull(apiScope.ServiceProvider.GetRequiredService<FlowRunService>());
    }
}
