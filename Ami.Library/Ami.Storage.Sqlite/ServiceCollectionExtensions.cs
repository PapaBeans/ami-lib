using Ami.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ami.Core.Storage;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the SQLite storage provider for the AMI library.
    /// </summary>
    public static IServiceCollection AddAmiSqliteStorage(this IServiceCollection services, Action<AmiSqliteBuilder> configure)
    {
        var builder = new AmiSqliteBuilder();
        configure(builder);

        if (string.IsNullOrEmpty(builder.DatabasePath))
        {
            throw new InvalidOperationException("DatabasePath must be configured for AMI SQLite storage.");
        }

        services.AddSingleton<IAmiRepository>(sp =>
            new SqliteRepository(
                builder.DatabasePath,
                builder.EnableFts5,
                sp.GetRequiredService<ILogger<SqliteRepository>>())
        );

        return services;
    }
}

public class AmiSqliteBuilder
{
    public string? DatabasePath { get; set; }
    public bool EnableFts5 { get; set; } = true;
}