using Ami.Core.Model;

namespace Ami.Core.Abstractions;

public interface IAmiRepository : IAsyncDisposable, IDisposable
{
    Task UpsertManuscriptAsync(Manuscript m, CancellationToken ct = default);
    Task<IReadOnlyList<Manuscript>> ListManuscriptsAsync(CancellationToken ct = default);
    Task BulkReplaceNodesAsync(string manuscriptId, IAsyncEnumerable<NodeRecord> nodes, CancellationToken ct);
    Task<ResolvedValue?> ResolveAsync(string manuscriptId, string key, CancellationToken ct);
    Task<IReadOnlyList<LineageHit>> TraceAsync(string manuscriptId, string key, CancellationToken ct);
    Task<IReadOnlyList<Node>> FindByKeyAsync(string key, CancellationToken ct);
    Task<IReadOnlyList<Node>> SearchAsync(NodeQuery q, CancellationToken ct);
    Task<IReadOnlyList<Node>> FtsAsync(string matchQuery, int limit, CancellationToken ct);
    string ConnectionString { get; }
}