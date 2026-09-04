using System.Globalization;
using System.Net;
using GoldSrcOps.Application.Availability;

namespace GoldSrcOps.AvailabilityExporter;

internal static class Program
{
    public static Task<int> Main(string[] args) => ConsoleRunner.RunAsync(args, CancellationToken.None);
}

internal static class ConsoleRunner
{
    private const int DefaultMaximumResponseBytes = 8 * 1024 * 1024;

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Contains("--help", StringComparer.Ordinal) ||
            args.Contains("-h", StringComparer.Ordinal))
        {
            PrintUsage();
            return 0;
        }

        if (!CommandOptionsParser.TryParse(args, out var options, out var error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine();
            PrintUsage();
            return 2;
        }

        try
        {
            return options switch
            {
                ExportCommandOptions export => await RunExportAsync(export, cancellationToken).ConfigureAwait(false),
                EvaluateCommandOptions evaluate => await RunEvaluationAsync(evaluate, cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidOperationException("The command is unsupported."),
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Operation failed: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> RunExportAsync(
        ExportCommandOptions command,
        CancellationToken cancellationToken)
    {
        var endpointText = ReadRequiredEnvironmentVariable("GOLDSRCOPS_GRAFANA_METRICS_URL");
        var user = ReadRequiredEnvironmentVariable("GOLDSRCOPS_GRAFANA_METRICS_USER");
        var token = ReadRequiredEnvironmentVariable("GOLDSRCOPS_GRAFANA_METRICS_TOKEN");

        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint))
        {
            throw new InvalidOperationException("The metrics API URL is invalid.");
        }

        var apiOptions = new GrafanaMetricsApiOptions(
            endpoint,
            user,
            token,
            command.Job,
            command.Probe,
            command.Environment,
            command.Role,
            command.MonitorRevision,
            command.Location,
            command.QueryStep,
            TimeSpan.FromSeconds(30),
            DefaultMaximumResponseBytes);

        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.Brotli |
                DecompressionMethods.Deflate |
                DecompressionMethods.GZip,
            MaxConnectionsPerServer = 2,
            MaxResponseHeadersLength = 64,
            UseCookies = false,
        };
        using var httpClient = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var client = new GrafanaMetricsApiClient(httpClient, apiOptions);
        var exporter = new GrafanaMetricsExporter(client, apiOptions);
        var records = await exporter.ExportAsync(
            command.WindowStartUtc - command.Overlap,
            command.WindowEndUtc,
            cancellationToken).ConfigureAwait(false);

        await AvailabilityJsonFile.WriteJsonLinesNewAsync(
            command.OutputPath,
            records,
            cancellationToken).ConfigureAwait(false);

        Console.WriteLine(FormattableString.Invariant(
            $"Export completed: {records.Count} normalized records written to a create-only segment."));
        return 0;
    }

    private static async Task<int> RunEvaluationAsync(
        EvaluateCommandOptions command,
        CancellationToken cancellationToken)
    {
        var records = await AvailabilityJsonFile.ReadJsonLinesAsync(
            command.InputPath,
            cancellationToken).ConfigureAwait(false);
        var report = AvailabilityEvaluator.Evaluate(records, command.Request);

        if (command.OutputPath is not null)
        {
            await AvailabilityJsonFile.WriteJsonNewAsync(
                command.OutputPath,
                report,
                cancellationToken).ConfigureAwait(false);
        }

        Console.WriteLine(FormattableString.Invariant(
            $"Expected slots: {report.ExpectedSlotCount}; evaluated: {report.EvaluatedSlotCount}; pending: {report.PendingSlotCount}."));
        Console.WriteLine(FormattableString.Invariant(
            $"Good: {report.GoodSlotCount}; bad: {report.BadSlotCount}; missing: {report.MissingSlotCount}."));
        Console.WriteLine(report.Availability is null
            ? "Availability: pending."
            : FormattableString.Invariant($"Availability: {report.Availability:P3}."));
        Console.WriteLine(report.MeetsTarget switch
        {
            true => "Target result: met.",
            false => "Target result: missed.",
            null => "Target result: pending.",
        });
        return 0;
    }

    private static string ReadRequiredEnvironmentVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Required environment variable {name} is not set.");
        }

        return value;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("GoldSrcOps availability evidence tool");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  export --window-start <UTC> --window-end <UTC> --job <label> --probe <label>");
        Console.WriteLine("         --role <primary|diagnostic> --monitor-revision <revision> --location <label>");
        Console.WriteLine("         --output <new.jsonl> [--environment production] [--overlap-minutes 10]");
        Console.WriteLine("         [--step-seconds 15]");
        Console.WriteLine();
        Console.WriteLine("  evaluate --input <file-or-directory> --window-start <UTC> --window-end <UTC>");
        Console.WriteLine("           --evaluated-at <UTC> --monitor-revision <revision> --location <label>");
        Console.WriteLine("           [--output <new.json>] [--grace-minutes 5] [--target 0.995]");
        Console.WriteLine();
        Console.WriteLine("Export credentials are read only from GOLDSRCOPS_GRAFANA_METRICS_URL,");
        Console.WriteLine("GOLDSRCOPS_GRAFANA_METRICS_USER, and GOLDSRCOPS_GRAFANA_METRICS_TOKEN.");
    }
}
