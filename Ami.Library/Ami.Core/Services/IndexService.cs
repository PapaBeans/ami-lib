using Ami.Core.Abstractions;
using Ami.Core.Model;
using Ami.Core.Parsing;
using Ami.Core.Util;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Runtime.InteropServices.Marshalling;
using Microsoft.Extensions.Logging;

namespace Ami.Core.Services;

public class IndexService : IAmiIndexService
{
    private readonly IAmiRepository _repository;
    private readonly ILogger<IndexService> _logger;
    private static bool IsCopy(string? name) =>
                        name?.TrimEnd().EndsWith("- Copy", StringComparison.OrdinalIgnoreCase) == true;

    public IndexService(IAmiRepository repository, ILogger<IndexService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task IndexAsync(IEnumerable<string> paths, AmiOptions options, CancellationToken ct = default)
    {
        using var activity = AmiInstrumentation.Source.StartActivity("IndexBatch");
        var stopwatch = Stopwatch.StartNew();

        var pathList = paths.ToList();
        activity?.SetTag("ami.files.count", pathList.Count);
        activity?.SetTag("ami.parallelism", options.MaxParallelism);
        _logger.LogInformation("Starting indexing for {FileCount} files with parallelism {MaxParallelism}.", pathList.Count, options.MaxParallelism);

        try
        {
            var normalizer = new DefaultFieldNormalizer();

            var headerTasks = pathList.Select(p => ManuscriptParser.ParseAsync(p, options.Analyzers, normalizer, ct));
            var parseResults = await Task.WhenAll(headerTasks);
            var manuscriptMap = parseResults
            .Select(r => r.Item1)
            .Where(m => m != null && m!.Id != null)
            .GroupBy(m => m!.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.FirstOrDefault(m => !IsCopy(m!.Name)) ?? g.First()!, // Prefer original over names with "- Copy"
                StringComparer.OrdinalIgnoreCase);

            var manuscriptsWithDepth = ComputeDepths(manuscriptMap);

            _logger.LogInformation("Upserting {ManuscriptCount} manuscript records.", manuscriptsWithDepth.Count);
            foreach (var manuscript in manuscriptsWithDepth.OrderBy(m => m.Depth))
            {
                ct.ThrowIfCancellationRequested();
                await _repository.UpsertManuscriptAsync(manuscript, ct);
            }

            _logger.LogInformation("Parsing and inserting nodes for {FileCount} files.", parseResults.Length);
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = options.MaxParallelism, CancellationToken = ct };
            await Parallel.ForEachAsync(parseResults, parallelOptions, async (result, token) =>
            {
                var (manuscript, nodes) = result;
                _logger.LogDebug("Processing nodes for manuscript {ManuscriptId} from path {Path}.", manuscript.Id, manuscript.Path);
                await _repository.BulkReplaceNodesAsync(manuscript.Id, nodes, token);
            });

            CachingAmiResolveService.InvalidateCache();
            _logger.LogInformation("Resolve cache invalidated.");

            AmiInstrumentation.ManuscriptsIndexed.Add(pathList.Count);
            stopwatch.Stop();
            AmiInstrumentation.IndexingDuration.Record(stopwatch.Elapsed.TotalSeconds);
            _logger.LogInformation("Indexing of {FileCount} files completed successfully in {ElapsedMilliseconds}ms.", pathList.Count, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Indexing operation was canceled.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An unhandled exception occurred during indexing.");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    private List<Manuscript> ComputeDepths(IDictionary<string, Manuscript> manuscriptMap)
    {
        var depthCache = new ConcurrentDictionary<string, int>();
        var resultList = new List<Manuscript>();

        foreach (var manuscript in manuscriptMap.Values)
        {
            var depth = GetDepth(manuscript.Id, manuscriptMap, depthCache);
            resultList.Add(manuscript with { Depth = depth });
        }

        return resultList;
    }

    private int GetDepth(string manuscriptId, IDictionary<string, Manuscript> manuscriptMap, ConcurrentDictionary<string, int> cache)
    {
        if (cache.TryGetValue(manuscriptId, out var cachedDepth)) return cachedDepth;
        if (!manuscriptMap.TryGetValue(manuscriptId, out var manuscript)) return 0;
        if (manuscript.ParentId == null || !manuscriptMap.ContainsKey(manuscript.ParentId))
        {
            cache[manuscriptId] = 0;
            return 0;
        }

        var visited = new HashSet<string> { manuscriptId };
        var current = manuscript;
        var depth = 0;
        while (current.ParentId != null && manuscriptMap.TryGetValue(current.ParentId, out var parent))
        {
            if (cache.TryGetValue(parent.Id, out var parentDepth))
            {
                depth += parentDepth + 1;
                goto Found;
            }
            if (!visited.Add(parent.Id))
            {
                _logger.LogWarning("Inheritance cycle detected involving manuscript {ManuscriptId}. Treating as a root.", manuscript.Id);
                depth = 0;
                goto Found;
            }
            depth++;
            current = parent;
        }

    Found:
        cache[manuscriptId] = depth;
        return depth;
    }
}