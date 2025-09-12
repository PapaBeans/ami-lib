using Ami.Core.Abstractions;
using Ami.Core.Model;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Ami.Core.Services;

/// <summary>
/// A decorator for IAmiResolveService that adds an in-memory cache for ResolveAsync calls.
/// This service should be registered as the primary IAmiResolveService, wrapping the real one.
/// </summary>
public class CachingAmiResolveService : IAmiResolveService
{
    private readonly IAmiResolveService _innerService;
    private readonly IMemoryCache _cache;
    private readonly ILogger<CachingAmiResolveService> _logger;
    
    // This token source is used to expire all cache entries at once after an index operation.
    private static CancellationTokenSource _resetCacheToken = new();

    public CachingAmiResolveService(IAmiResolveService innerService, IMemoryCache cache, ILogger<CachingAmiResolveService> logger)
    {
        _innerService = innerService;
        _cache = cache;
        _logger = logger;
    }

    public async Task<ResolvedValue?> ResolveAsync(string manuscriptId, string key, CancellationToken ct = default)
    {
        var cacheKey = $"resolve::{manuscriptId}::{key}";
        
        if (_cache.TryGetValue(cacheKey, out ResolvedValue? cachedValue))
        {
            _logger.LogTrace("Cache hit for key {CacheKey}.", cacheKey);
            return cachedValue;
        }

        _logger.LogTrace("Cache miss for key {CacheKey}. Resolving from repository.", cacheKey);
        var resolvedValue = await _innerService.ResolveAsync(manuscriptId, key, ct);

        var options = new MemoryCacheEntryOptions()
            .SetSlidingExpiration(TimeSpan.FromMinutes(10))
            .AddExpirationToken(new CancellationChangeToken(_resetCacheToken.Token));
        
        _cache.Set(cacheKey, resolvedValue, options);

        return resolvedValue;
    }

    /// <summary>
    /// Tracing is not cached as it's typically a diagnostic operation.
    /// </summary>
    public Task<IReadOnlyList<LineageHit>> TraceAsync(string manuscriptId, string key, CancellationToken ct = default)
    {
        return _innerService.TraceAsync(manuscriptId, key, ct);
    }

    /// <summary>
    /// Clears the entire resolve cache. This should be called after any indexing operation.
    /// </summary>
    public static void InvalidateCache()
    {
        if (!_resetCacheToken.IsCancellationRequested)
        {
            _resetCacheToken.Cancel();
            _resetCacheToken.Dispose();
        }
        _resetCacheToken = new CancellationTokenSource();
    }
}