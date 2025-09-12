using Ami.Core.Abstractions;
using Ami.Core.Model;

namespace Ami.Core.Services;

/// <summary>
/// The concrete implementation of the resolve service. It directly uses the repository.
/// This class is intended to be decorated by the CachingAmiResolveService.
/// </summary>
public class ResolveService : IAmiResolveService
{
    private readonly IAmiRepository _repository;
    public ResolveService(IAmiRepository repository) => _repository = repository;

    public Task<ResolvedValue?> ResolveAsync(string manuscriptId, string key, CancellationToken ct = default) =>
        _repository.ResolveAsync(manuscriptId, key, ct);

    public Task<IReadOnlyList<LineageHit>> TraceAsync(string manuscriptId, string key, CancellationToken ct = default) =>
        _repository.TraceAsync(manuscriptId, key, ct);
}