using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace Ami.Core.Util;

/// <summary>
/// Provides a single point of entry for creating OpenTelemetry-compatible
/// activities (traces) and metrics.
/// </summary>
public static class AmiInstrumentation
{
    private static readonly AssemblyName AssemblyName = typeof(AmiInstrumentation).Assembly.GetName();
    private static readonly string? Version = AssemblyName.Version?.ToString();

    /// <summary>
    /// The ActivitySource for creating all traces within the AMI library.
    /// </summary>
    public static readonly ActivitySource Source = new(AssemblyName.Name!, Version);

    /// <summary>
    /// The Meter for creating all metrics within the AMI library.
    /// </summary>
    public static readonly Meter Meter = new(AssemblyName.Name!, Version);

    public static readonly Counter<long> ManuscriptsIndexed =
        Meter.CreateCounter<long>("ami.manuscripts.indexed.count", "manuscripts", "The number of manuscripts successfully indexed.");

    public static readonly Counter<long> NodesInserted =
        Meter.CreateCounter<long>("ami.nodes.inserted.count", "nodes", "The number of nodes inserted into the index.");
    
    public static readonly Histogram<double> IndexingDuration =
        Meter.CreateHistogram<double>("ami.indexing.duration.seconds", "s", "The duration of a full indexing operation in seconds.");
}