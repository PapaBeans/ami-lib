using Ami.Core;
using Ami.Core.Abstractions;
using Ami.Core.Model;
using Ami.Core.Parsing;
using Ami.Core.Services;
using Ami.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Serilog;
using System.Diagnostics;

#region Main Application Loop
// --- Configuration ---
var dbPath = "ami.cli.db";

// --- Create DI Host ---
using var host = CreateHost(dbPath);
AnsiConsole.MarkupLine($"[bold green]Database initialized at:[/] [dim]{Path.GetFullPath(dbPath)}[/]");
AnsiConsole.MarkupLine($"[bold yellow]Verbose logs are being written to:[/] [dim]ami-cli-log.txt[/]");

// --- Main Menu Loop ---
var keepRunning = true;
while (keepRunning)
{
    AnsiConsole.Clear();
    AnsiConsole.Write(new FigletText("AMI Library").Centered().Color(Color.Blue));
    AnsiConsole.Write(new Rule("[bold]Main Menu[/]").Centered());

    var choice = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("Select an operation:")
            .PageSize(10)
            .AddChoices(new[]
            {
                "Index Manuscript Directory",
                "Resolve a Value",
                "Trace a Value's Lineage",
                "Full-Text Search",
                "Exit"
            }));

    switch (choice)
    {
        case "Index Manuscript Directory":
            await HandleIndexAsync(host.Services);
            break;
        case "Resolve a Value":
            await HandleResolveAsync(host.Services);
            break;
        case "Trace a Value's Lineage":
            await HandleTraceAsync(host.Services);
            break;
        case "Full-Text Search":
            await HandleSearchAsync(host.Services);
            break;
        case "Exit":
            keepRunning = false;
            break;
    }

    if (keepRunning)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]Press any key to return to the menu...[/]");
        Console.ReadKey();
    }
}

AnsiConsole.MarkupLine("[bold blue]Goodbye![/]");
#endregion

#region Action Handlers

async Task HandleIndexAsync(IServiceProvider services)
{
    var indexService = services.GetRequiredService<IAmiIndexService>();
    var logger = services.GetRequiredService<ILogger<Program>>();

    var path = AnsiConsole.Prompt(
        new TextPrompt<string>("[yellow]Enter the path to the manuscript directory:[/] ")
            .Validate(p => Directory.Exists(p) ? ValidationResult.Success() : ValidationResult.Error("[red]Directory does not exist[/]")));

    var options = new AmiOptions
    {
        Analyzers = new[] { new ComparisonValueAnalyzer() },
        MaxParallelism = Environment.ProcessorCount
    };

    var files = Directory.GetFiles(path, "*.xml", SearchOption.AllDirectories);
    if (!files.Any())
    {
        AnsiConsole.MarkupLine("[bold red]No .xml files found in the specified directory.[/]");
        return;
    }

    try
    {
        var stopwatch = Stopwatch.StartNew();
        var indexingTask = indexService.IndexAsync(files, options);

        await AnsiConsole.Status()
            .StartAsync($"[yellow]Indexing...[/]", async ctx =>
            {
                // This internal loop updates the status text with elapsed time
                // while the main indexing task is running
                while (!indexingTask.IsCompleted)
                {
                    ctx.Status($"[yellow]Indexing {files.Length} files... ({stopwatch.Elapsed:m\\:ss})[/]");
                    ctx.Refresh();
                    await Task.Delay(500);
                }
            });
        await indexingTask;
        stopwatch.Stop();

        AnsiConsole.MarkupLine($"[bold green] Indexing completed successfully in {stopwatch.Elapsed:g}.[/]");
    }
    catch (Exception ex)
    {
        // Log the full, verbose exception to the log file
        logger.LogError(ex, "A critical error occurred during the indexing operation.");

        //Show a user friendly message in the console
        AnsiConsole.MarkupLine("[bold red]An error occurred during indexing:[/]");
        AnsiConsole.MarkupLine("[red]See the ami0cli-log.txt file for complete details.[/]");
        AnsiConsole.WriteException(ex, ExceptionFormats.ShortenTypes | ExceptionFormats.ShortenPaths);
    }
}

async Task HandleResolveAsync(IServiceProvider services)
{
    var resolveService = services.GetRequiredService<IAmiResolveService>();

    var manuscriptId = AnsiConsole.Ask<string>("[yellow]Enter the Manuscript ID (e.g., Manuscript_Data_1_0_0_0):[/]");
    var key = AnsiConsole.Ask<string>("[yellow]Enter the Key to resolve (e.g., Policy.Surcharge):[/]");

    ResolvedValue? result = null;
    await AnsiConsole.Status()
        .StartAsync("[yellow]Resolving...[/]", async ctx =>
        {
            result = await resolveService.ResolveAsync(manuscriptId, key);
        });

    if (result is null)
    {
        AnsiConsole.MarkupLine($"[bold red]Could not resolve key '{key}' for manuscript '{manuscriptId}'.[/]");
        return;
    }

    var table = new Table().Border(TableBorder.Rounded).Expand();
    table.AddColumn("[bold]Property[/]");
    table.AddColumn("[bold]Value[/]");
    table.AddRow("Source Manuscript", $"[green]{result.SourceManuscriptId}[/]");
    table.AddRow("Value (Text)", EscapeMarkup(result.ValueText) ?? "[dim](none)[/]");
    table.AddRow("Value (XML)", EscapeMarkup(result.ValueXml) ?? "[dim](none)[/]");

    AnsiConsole.Write(table);
}

async Task HandleTraceAsync(IServiceProvider services)
{
    var resolveService = services.GetRequiredService<IAmiResolveService>();

    var manuscriptId = AnsiConsole.Ask<string>("[yellow]Enter the Manuscript ID (e.g., PA_2_0_0):[/]");
    var key = AnsiConsole.Ask<string>("[yellow]Enter the Key to trace (e.g., Policy.Surcharge):[/]");

    IReadOnlyList<LineageHit> results = new List<LineageHit>();
    await AnsiConsole.Status()
        .StartAsync("[yellow]Tracing lineage...[/]", async ctx =>
        {
            results = await resolveService.TraceAsync(manuscriptId, key);
        });

    if (!results.Any())
    {
        AnsiConsole.MarkupLine($"[bold red]No lineage found for key '{key}' starting from manuscript '{manuscriptId}'.[/]");
        return;
    }

    var table = new Table().Border(TableBorder.Rounded).Expand();
    table.Title($"[bold blue]Lineage Trace for '{key}'[/]");
    table.AddColumn("[bold]Depth[/]");
    table.AddColumn("[bold]Manuscript ID[/]");
    table.AddColumn("[bold]Defined Value[/]");

    foreach (var hit in results)
    {
        var value = EscapeMarkup(hit.ValueText ?? hit.ValueXml) ?? "[dim](no value defined)[/]";
        table.AddRow($"[yellow]{hit.Depth}[/]", $"[green]{hit.ManuscriptId}[/]", value);
    }

    AnsiConsole.Write(table);
}

async Task HandleSearchAsync(IServiceProvider services)
{
    var searchService = services.GetRequiredService<IAmiSearchService>();

    var query = AnsiConsole.Ask<string>("[yellow]Enter FTS query (e.g., '\"BaseFactor\" OR Eligible'):[/]");

    IReadOnlyList<Node> results = new List<Node>();
    await AnsiConsole.Status()
        .StartAsync("[yellow]Searching...[/]", async ctx =>
        {
            results = await searchService.FtsAsync(query, limit: 50);
        });

    if (!results.Any())
    {
        AnsiConsole.MarkupLine("[bold red]No results found.[/]");
        return;
    }

    var table = new Table().Border(TableBorder.Rounded).Expand();
    table.Title($"[bold blue]Search Results for '{query}' ({results.Count})[/]");
    table.AddColumn("[bold]Manuscript[/]");
    table.AddColumn("[bold]Key[/]");
    table.AddColumn("[bold]Value[/]");

    foreach (var node in results)
    {
        var value = EscapeMarkup(node.ValueText ?? node.ValueXml) ?? "[dim](none)[/]";
        table.AddRow($"[green]{node.ManuscriptId}[/]", $"[yellow]{node.Key}[/]", Truncate(value, 80));
    }

    AnsiConsole.Write(table);
}

#endregion

#region DI Host Setup & Utilities
IHost CreateHost(string dbPath)
{
    return Host.CreateDefaultBuilder()
        .UseSerilog((context, services, configuration) => configuration
        .WriteTo.File(
                "ami-cli-log.txt",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
            )
        )
        .ConfigureServices((_, services) =>
        {
            services.AddAmiSqliteStorage(sqlite =>
            {
                sqlite.DatabasePath = dbPath;
                sqlite.EnableFts5 = true;
            });
            services.AddAmiCore();
        })
        .Build();
}

string Truncate(string? value, int maxLength)
{
    if (string.IsNullOrEmpty(value)) return "(none)";
    return value.Length <= maxLength ? value : value.Substring(0, maxLength) + "...";
}

string? EscapeMarkup(string? value)
{
    return value?.Replace("[", "[[").Replace("]", "]]");
}
#endregion