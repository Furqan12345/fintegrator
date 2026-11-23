using System;
using Microsoft.Extensions.DependencyInjection;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models.Storage;
using SimpleIPaaS.Infrastructure.Elasticsearch;

namespace SimpleIPaaS.Infrastructure.Storage;

// One place both hosts wire storage from, so the Api and the Engine cannot drift apart in which
// backend serves which dataset.
public static class StorageServiceCollectionExtensions
{
    public static IServiceCollection AddIPaaSStorage(this IServiceCollection services)
    {
        // Settings + Elasticsearch plumbing (singletons: they cache and hold connections).
        // Constructed explicitly so the optional cache-interval parameter can never confuse the
        // container's constructor selection. There is no ILogger here on purpose: the log sink
        // depends on this provider to choose its backend, and ILogger<T> resolves through every
        // ILoggerProvider — logging storage failures from inside that graph would be a cycle the
        // container's validation walks forever.
        services.AddSingleton<IStorageSettingsProvider>(provider => new StorageSettingsProvider(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IEncryptionService>()));
        // Routing state read by every hot path, refreshed by its own background loop (it is also the
        // hosted service below). Registered before anything that reads it.
        services.AddSingleton(provider => new StorageRuntime(provider));
        services.AddHostedService(provider => provider.GetRequiredService<StorageRuntime>());

        services.AddSingleton<ElasticsearchClientProvider>();
        services.AddSingleton<ElasticsearchIndexManager>();
        services.AddSingleton<ElasticsearchLogRepository>();
        services.AddSingleton<StorageRetentionService>();
        services.AddSingleton<IStorageOperations, StorageOperations>();

        // The log read path routes per call: the console must follow a dataset that is moved
        // between backends while the host is running.
        services.AddScoped<SimpleIPaaS.Infrastructure.Persistence.LogRepository>();
        services.AddScoped<ILogRepository>(provider => new RoutingLogRepository(
            provider.GetRequiredService<SimpleIPaaS.Infrastructure.Persistence.LogRepository>(),
            provider.GetRequiredService<ElasticsearchLogRepository>(),
            provider.GetRequiredService<IStorageSettingsProvider>()));

        // Growing datasets that can live in either store.
        services.AddScoped<SqlPacketLogStore>();
        services.AddScoped<ElasticPacketLogStore>();
        services.AddScoped(provider => new StorageStoreRouter<IPacketLogStore>(
            provider.GetRequiredService<SqlPacketLogStore>(),
            provider.GetRequiredService<ElasticPacketLogStore>(),
            provider.GetRequiredService<IStorageSettingsProvider>(),
            StorageDatasets.PacketLogs));

        services.AddScoped<SqlCrossReferenceEntryStore>();
        services.AddScoped<ElasticCrossReferenceEntryStore>();
        services.AddScoped(provider => new StorageStoreRouter<ICrossReferenceEntryStore>(
            provider.GetRequiredService<SqlCrossReferenceEntryStore>(),
            provider.GetRequiredService<ElasticCrossReferenceEntryStore>(),
            provider.GetRequiredService<IStorageSettingsProvider>(),
            StorageDatasets.CrossReferences));

        services.AddScoped<StorageSettingsWriter>();

        return services;
    }
}
