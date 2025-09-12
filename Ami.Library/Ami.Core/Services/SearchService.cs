using Ami.Core.Abstractions;
using Ami.Core.Model;

namespace Ami.Core.Services;

public class SearchService : IAmiSearchService
{
    private readonly IAmiRepository _repository;
    public SearchService(IAmiRepository repository) => _repository = repository;

    public Task<IReadOnlyList<Node>> FindByKeyAsync(string key, CancellationToken ct = default) =>
        _repository.FindByKeyAsync(key, ct);

    public Task<IReadOnlyList<Node>> SearchAsync(NodeQuery q, CancellationToken ct = default) =>
        _repository.SearchAsync(q, ct);

    public Task<IReadOnlyList<Node>> FtsAsync(string matchQuery, int limit = 500, CancellationToken ct = default) =>
        _repository.FtsAsync(matchQuery, limit, ct);
}