using Microsoft.Extensions.DependencyInjection;
using Ami.Core.Abstractions;
using Ami.Core.Parsing;
using Ami.Core.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Ami.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the complete AMI Core library to the service collection.
    /// Requires a storage provider (e.g., Ami.Storage.Sqlite) to be registered separately.
    /// </summary>
    public static IServiceCollection AddAmiCore(this IServiceCollection services)
    {
        // Core services
        services.AddSingleton<IAmiIndexService, IndexService>();
        services.AddSingleton<IAmiSearchService, SearchService>();
        services.AddSingleton<IFieldNormalizer, DefaultFieldNormalizer>();
        services.AddSingleton<IAmiTransformService, TransformService>();
        
        // Register the concrete resolve service so the decorator can find it.
        services.AddSingleton<ResolveService>();
        services.AddSingleton<IMemoryCache, MemoryCache>();
        
        // Decorate the concrete service with the caching layer.
        services.AddSingleton<IAmiResolveService>(sp => 
            new CachingAmiResolveService(
                sp.GetRequiredService<ResolveService>(), 
                sp.GetRequiredService<IMemoryCache>(),
                sp.GetRequiredService<ILogger<CachingAmiResolveService>>())
        );

        return services;
    }
}