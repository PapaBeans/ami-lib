namespace Ami.Core.Abstractions;

/// <summary>
/// Provides immutable configuration options for AMI services.
/// </summary>
public sealed class AmiOptions
{
    /// <summary>
    /// A registry of value analyzers to be run during parsing. The order matters.
    /// </summary>
    public IReadOnlyList<IValueAnalyzer> Analyzers { get; init; }

    /// <summary>
    /// The maximum number of files to process in parallel during an indexing operation.
    /// </summary>
    public int MaxParallelism { get; init; }

    /// <summary>
    /// Specifies whether to enable and build the FTS5 full-text search index.
    /// </summary>
    public bool EnableFts5 { get; init; }

    public AmiOptions()
    {
        Analyzers = new List<IValueAnalyzer>();
        MaxParallelism = Math.Max(1, Environment.ProcessorCount / 2);
        EnableFts5 = true;
    }
}