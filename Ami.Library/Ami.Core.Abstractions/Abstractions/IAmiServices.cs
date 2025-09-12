using Ami.Core.Model;

namespace Ami.Core.Abstractions;

public interface IAmiIndexService
{
    Task IndexAsync(IEnumerable<string> paths, AmiOptions options, CancellationToken ct = default);
}

public interface IAmiResolveService
{
    Task<ResolvedValue?> ResolveAsync(string manuscriptId, string key, CancellationToken ct = default);
    Task<IReadOnlyList<LineageHit>> TraceAsync(string manuscriptId, string key, CancellationToken ct = default);
}

public interface IAmiSearchService
{
    Task<IReadOnlyList<Node>> FindByKeyAsync(string key, CancellationToken ct = default);
    Task<IReadOnlyList<Node>> SearchAsync(NodeQuery q, CancellationToken ct = default);
    Task<IReadOnlyList<Node>> FtsAsync(string matchQuery, int limit = 500, CancellationToken ct = default);
}

public interface IAmiTransformService
{
    Task<int> SetValueXmlAsync(string manuscriptPath, string key, string newValueXml, CancellationToken ct = default);
    Task<int> SetValueAttributesAsync(string manuscriptPath, string key, IReadOnlyDictionary<string, string> newAttributes, CancellationToken ct = default);
}