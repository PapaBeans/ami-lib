# AMI Library - Author Manuscript Indexer

[![Build Status](https://img.shields.io/badge/build-passing-brightgreen)](https://github.com)
[![NuGet](https://img.shields.io/nuget/v/Ami.Core.Abstractions.svg)](https://nuget.org)
[![License](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

A high-performance, enterprise-grade .NET 8 library for indexing, searching, and transforming Duck Creek Author Manuscripts.

---

## What is the AMI Library?

The Author Manuscript Indexer (AMI) is a pure .NET class library designed to handle the complexities of Duck Creek's XML-based manuscript format. It provides a robust solution for:

*   **Indexing:** Parsing large collections of manuscript files, understanding their inheritance, and storing their data in a structured, searchable format.
*   **Resolving:** Calculating the "effective" value of any field for a given manuscript by walking its inheritance chain.
*   **Searching:** Performing structured queries and powerful full-text searches across the entire manuscript corpus.
*   **Transforming:** Safely modifying values within manuscript XML files.

It is built with performance, scalability, and maintainability in mind, using streaming parsers, a high-performance SQLite backend, and a clean, decoupled architecture.

## Features

-   **Streaming XML Parsing:** Indexes large manuscript files with a minimal memory footprint using `XmlReader`.
-   **Inheritance-Aware Resolution:** Correctly resolves field values by traversing the parent-child manuscript hierarchy.
-   **SQLite Backend with FTS5:** Uses a local, high-performance SQLite database for storage, with optional FTS5 support for powerful full-text search capabilities.
-   **Pluggable Analyzers:** Extensible system to analyze complex `<value>` XML fragments (e.g., `<comparison>` blocks) and make them searchable.
-   **Dependency Injection Friendly:** Designed for modern .NET applications with clean service registration extensions.
-   **Enterprise Grade:** Includes support for structured logging, metrics, and tracing via OpenTelemetry standards.
-   **Decoupled Architecture:** Separated into `Abstractions`, `Core`, and `Storage` layers, allowing for future storage providers.

## Getting Started

### Installation

The AMI Library is distributed as a set of NuGet packages. You will typically need the core library and a storage provider.

```bash
# Install the core services and abstractions
dotnet add package Ami.Core

# Install the SQLite storage provider
dotnet add package Ami.Storage.Sqlite
```

### Usage

The library is designed to be used with a dependency injection container. The following example uses the .NET Generic Host.

#### 1. Configuration

In your `Program.cs` or `Startup.cs`, configure the AMI services.

```csharp
using Ami.Core;
using Ami.Core.Abstractions;
using Ami.Core.Parsing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;

var builder = Host.CreateDefaultBuilder(args);

builder.ConfigureServices((context, services) =>
{
    // 1. Add the SQLite storage provider, specifying the database path.
    services.AddAmiSqliteStorage(sqlite =>
    {
        sqlite.DatabasePath = "ami.db";
        sqlite.EnableFts5 = true;
    });

    // 2. Add the core AMI services (indexing, searching, etc.).
    services.AddAmiCore();
    
    // 3. (Optional) Add your application's services that will use AMI.
    services.AddHostedService<MyManuscriptProcessor>();
});

var host = builder.Build();
await host.RunAsync();
```

#### 2. Indexing Manuscripts

Inject `IAmiIndexService` and call `IndexAsync` to process your manuscript files.

```csharp
using Ami.Core.Abstractions;
using Ami.Core.Parsing;
using Microsoft.Extensions.Logging;

public class MyManuscriptProcessor
{
    private readonly IAmiIndexService _indexService;
    private readonly ILogger<MyManuscriptProcessor> _logger;

    public MyManuscriptProcessor(IAmiIndexService indexService, ILogger<MyManuscriptProcessor> logger)
    {
        _indexService = indexService;
        _logger = logger;
    }

    public async Task ProcessFilesAsync(string directoryPath)
    {
        _logger.LogInformation("Starting manuscript indexing...");

        var files = Directory.GetFiles(directoryPath, "*.xml", SearchOption.AllDirectories);

        var options = new AmiOptions
        {
            // Register custom analyzers for complex value types
            Analyzers = new[] { new ComparisonValueAnalyzer() },
            MaxParallelism = Environment.ProcessorCount
        };

        await _indexService.IndexAsync(files, options);

        _logger.LogInformation("Indexing complete.");
    }
}
```

#### 3. Resolving Values

Inject `IAmiResolveService` to find the effective value of a field or trace its lineage.

```csharp
public class MyValueResolver
{
    private readonly IAmiResolveService _resolveService;

    public MyValueResolver(IAmiResolveService resolveService)
    {
        _resolveService = resolveService;
    }

    public async Task<ResolvedValue?> GetEffectiveValue(string manuscriptId, string key)
    {
        // Example: Get the effective value of 'Policy.Surcharge' for manuscript 'PA_2_0_0'
        return await _resolveService.ResolveAsync(manuscriptId, key);
    }

    public async Task<IReadOnlyList<LineageHit>> GetValueLineage(string manuscriptId, string key)
    {
        // Traces the value from the child up to the root, showing all overrides.
        return await _resolveService.TraceAsync(manuscriptId, key);
    }
}
```

#### 4. Searching

Inject `IAmiSearchService` to perform structured or full-text searches.

```csharp
public class MyManuscriptSearch
{
    private readonly IAmiSearchService _searchService;

    public MyManuscriptSearch(IAmiSearchService searchService)
    {
        _searchService = searchService;
    }

    public async Task<IReadOnlyList<Node>> FindSurchargeFields()
    {
        // Structured search
        var query = new NodeQuery(Key: "Policy.Surcharge", ObjectName: "Policy");
        return await _searchService.SearchAsync(query);
    }

    public async Task<IReadOnlyList<Node>> FullTextSearch(string term)
    {
        // FTS5 search (if enabled)
        // Example term: '"BaseFactor" OR Eligible'
        return await _searchService.FtsAsync(term, limit: 100);
    }
}
```

#### 5. Transforming Manuscripts

Inject `IAmiTransformService` to modify manuscript files on disk.

**Important:** After transforming a file, you must re-index it for the changes to be reflected in the search index.

```csharp
public class MyManuscriptUpdater
{
    private readonly IAmiTransformService _transformService;
    private readonly IAmiIndexService _indexService; // Needed for re-indexing

    public MyManuscriptUpdater(IAmiTransformService transformService, IAmiIndexService indexService)
    {
        _transformService = transformService;
        _indexService = indexService;
    }

    public async Task UpdateBaseFactor(string filePath)
    {
        string key = "Rating.BaseFactor";
        string newValueXml = "<value value=\"1.25\" />";

        int updates = await _transformService.SetValueXmlAsync(filePath, key, newValueXml);

        if (updates > 0)
        {
            // Re-index the single file that was changed
            await _indexService.IndexAsync(new[] { filePath }, new AmiOptions());
        }
    }
}
```

## Domain Context

The library is designed around these core Duck Creek Author Manuscript concepts:

-   **Manuscripts:** XML files identified by a `manuscriptId`. They can inherit from a parent via the `inherited` attribute.
-   **Objects:** Represented by `<object id="...">`, these are containers for fields.
-   **Fields:** Represented by `<public id="...">` or `<private id="...">`, these are the individual data points.
-   **Key Convention:** A field's unique key is a combination of its parent object's ID and its own ID, formatted as `ObjectId.FieldId` (e.g., `Policy.Surcharge`).
-   **Inheritance:** A child manuscript only stores values that override its parent. Resolving a value means finding the closest definition in the inheritance chain, starting from the child itself.

## Architectural Overview

The solution is divided into three primary projects:

-   **`Ami.Core.Abstractions`**: Contains all public interfaces (`IAmiRepository`, `IAmiIndexService`), domain models, and exceptions. This is the public contract of the library.
-   **`Ami.Core`**: The main implementation library. It contains the streaming parser, service orchestrators, DI extensions, and observability helpers. It is storage-agnostic and depends only on the abstractions.
-   **`Ami.Storage.Sqlite`**: A concrete storage implementation using `Microsoft.Data.Sqlite`. It provides the `SqliteRepository` which implements `IAmiRepository`.

## Building from Source

1.  Clone the repository.
2.  Ensure you have the .NET 8 SDK installed.
3.  Run `dotnet build` from the root directory.

## Contributing

Contributions are welcome! Please open an issue to discuss your idea or submit a pull request.

## License

This project is licensed under the MIT License. See the [LICENSE](LICENSE) file for details. 

---

### **`Ami.Cli` - A Console Application Consumer**

This project demonstrates how to use the AMI Library. It is a separate executable that references the library projects.

It is set as the default startup project inside of the solution and should serve as a great starting point for anyone getting started with this library.

Feel free to reach out and ask any questions about it! dakota.mihelich@farmersinsurance.com