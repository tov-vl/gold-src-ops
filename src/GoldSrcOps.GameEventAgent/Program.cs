using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GoldSrcOps.GameEventAgent;

internal static class Program
{
    public static Task<int> Main(string[] args) =>
        GameEventAgentConsole.RunAsync(args, CancellationToken.None);
}

internal static class GameEventAgentConsole
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Contains("--help", StringComparer.Ordinal) ||
            args.Contains("-h", StringComparer.Ordinal))
        {
            PrintUsage();
            return 0;
        }

        if (!GameEventAgentCommandLine.TryParse(args, out var command, out var error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine();
            PrintUsage();
            return 2;
        }

        try
        {
            var builder = CreateApplicationBuilder();
            PrepareConfigurationForCommand(builder.Configuration, command);

            var options = GameEventAgentOptions.FromConfiguration(
                builder.Configuration,
                builder.Environment.ContentRootPath);

            return command switch
            {
                RunAgentCommand => await RunWorkerAsync(
                    builder,
                    options,
                    cancellationToken).ConfigureAwait(false),
                EnqueueAgentCommand enqueue => await EnqueueAsync(
                    options.Queue,
                    enqueue.InputPath,
                    cancellationToken).ConfigureAwait(false),
                WriteSpoolRecordCommand writeSpool => await WriteSpoolRecordAsync(
                    options.Spool,
                    writeSpool.InputPath,
                    cancellationToken).ConfigureAwait(false),
                ImportSpoolCommand => await ImportSpoolAsync(
                    options,
                    cancellationToken).ConfigureAwait(false),
                VerifyAccessTokenAgentCommand => await VerifyAccessTokenAsync(
                    builder,
                    options,
                    cancellationToken).ConfigureAwait(false),
                ShowQueueStatusCommand status => ShowStatus(options, status.Json),
                _ => throw new InvalidOperationException("The game-event agent command is unsupported.")
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Operation failed: {exception.Message}");
            return 1;
        }
    }

    private static HostApplicationBuilder CreateApplicationBuilder() =>
        Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory
        });

    internal static void PrepareConfigurationForCommand(
        IConfiguration configuration,
        GameEventAgentCommand command)
    {
        if (command is VerifyAccessTokenAgentCommand)
        {
            configuration["GameEventAgent:Delivery:Enabled"] = "true";
        }
    }

    private static async Task<int> RunWorkerAsync(
        HostApplicationBuilder builder,
        GameEventAgentOptions options,
        CancellationToken cancellationToken)
    {
        if (!options.Spool.Enabled && options.Delivery is null)
        {
            throw new InvalidOperationException(
                "Game-event spool import and delivery are disabled. Enable at least one component after supplying its complete sandbox configuration.");
        }

        builder.Services.AddSingleton(options.Queue);
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddSingleton<IGameEventOutbox, SqliteGameEventOutbox>();

        if (options.Spool.Enabled)
        {
            builder.Services.AddSingleton(options.Spool);
            builder.Services.AddSingleton<GameEventSpoolImporter>();
            builder.Services.AddHostedService<GameEventSpoolWorker>();
        }

        if (options.Delivery is GameEventDeliveryOptions delivery)
        {
            AddAccessTokenProvider(builder.Services, delivery);
            builder.Services.AddSingleton<IGameEventDeliveryClient, GameEventDeliveryClient>();
            builder.Services.AddSingleton<IRetryDelayPolicy, ExponentialRetryDelayPolicy>();
            builder.Services.AddSingleton<GameEventDispatcher>();
            builder.Services.AddHostedService<GameEventDeliveryWorker>();

            builder.Services
                .AddHttpClient(
                    GameEventDeliveryClient.HttpClientName,
                    client =>
                    {
                        client.BaseAddress = delivery.ApiBaseUri;
                        client.Timeout = delivery.RequestTimeout;
                    })
                .ConfigurePrimaryHttpMessageHandler(CreateHttpMessageHandler);
        }

        using var host = builder.Build();
        await host.RunAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> VerifyAccessTokenAsync(
        HostApplicationBuilder builder,
        GameEventAgentOptions options,
        CancellationToken cancellationToken)
    {
        var delivery = options.Delivery ?? throw new InvalidOperationException(
            "OAuth access-token preflight requires complete delivery configuration.");

        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        AddAccessTokenProvider(builder.Services, delivery);

        using var host = builder.Build();
        _ = await host.Services
            .GetRequiredService<IGameEventAccessTokenProvider>()
            .GetAccessTokenAsync(cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine("OAuth access-token preflight passed.");
        return 0;
    }

    private static void AddAccessTokenProvider(
        IServiceCollection services,
        GameEventDeliveryOptions delivery)
    {
        services.AddSingleton(delivery);
        services.AddSingleton(delivery.OAuth);
        services.AddSingleton<IGameEventAccessTokenProvider, ClientCredentialsAccessTokenProvider>();
        services
            .AddHttpClient(
                ClientCredentialsAccessTokenProvider.HttpClientName,
                client => client.Timeout = delivery.RequestTimeout)
            .ConfigurePrimaryHttpMessageHandler(CreateHttpMessageHandler);
    }

    private static async Task<int> EnqueueAsync(
        GameEventQueueOptions queueOptions,
        string inputPath,
        CancellationToken cancellationToken)
    {
        var input = await ReadSourceInputAsync(inputPath, cancellationToken).ConfigureAwait(false);

        var outbox = new SqliteGameEventOutbox(queueOptions);
        outbox.Initialize();
        var queued = outbox.Enqueue(input, TimeProvider.System.GetUtcNow());
        Console.WriteLine(FormattableString.Invariant(
            $"Event queued: sequence {queued.SequenceNumber}, event {queued.EventId:D}."));
        return 0;
    }

    private static async Task<int> WriteSpoolRecordAsync(
        GameEventSpoolOptions spoolOptions,
        string inputPath,
        CancellationToken cancellationToken)
    {
        var input = await ReadSourceInputAsync(inputPath, cancellationToken).ConfigureAwait(false);
        var writer = new GameEventSpoolWriter(spoolOptions, TimeProvider.System);
        await writer.WriteAsync(input, cancellationToken).ConfigureAwait(false);
        Console.WriteLine("Spool record written.");
        return 0;
    }

    private static async Task<int> ImportSpoolAsync(
        GameEventAgentOptions options,
        CancellationToken cancellationToken)
    {
        var outbox = new SqliteGameEventOutbox(options.Queue);
        var importer = new GameEventSpoolImporter(options.Spool, outbox, TimeProvider.System);
        var result = await importer.ImportBatchAsync(cancellationToken).ConfigureAwait(false);
        Console.WriteLine(FormattableString.Invariant(
            $"Spool import: imported={result.Imported}, reconciled={result.Reconciled}, finalized={result.Finalized}, rejected={result.Rejected}, deferred={result.Deferred}."));
        return result.Rejected == 0 && result.Deferred == 0 ? 0 : 1;
    }

    private static async Task<GameEventSourceInput> ReadSourceInputAsync(
        string inputPath,
        CancellationToken cancellationToken)
    {
        var absolutePath = Path.GetFullPath(inputPath, Directory.GetCurrentDirectory());
        var fileInfo = new FileInfo(absolutePath);
        if (!fileInfo.Exists || fileInfo.Length is <= 0 or > GameEventJson.MaximumRequestBytes)
        {
            throw new InvalidOperationException(
                "The test-source input file is missing, empty, or exceeds 4 KiB.");
        }

        await using var stream = new FileStream(
            absolutePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<GameEventSourceInput>(
            stream,
            GameEventJson.StrictSerializerOptions,
            cancellationToken).ConfigureAwait(false) ??
            throw new InvalidOperationException("The test-source input file contains no event.");
    }

    private static int ShowStatus(GameEventAgentOptions options, bool json)
    {
        var snapshot = GameEventAgentStatus.Capture(options);
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(snapshot, GameEventJson.SerializerOptions));
            return 0;
        }

        var statistics = snapshot.Queue;
        var spool = snapshot.Spool;
        Console.WriteLine(FormattableString.Invariant(
            $"Queue status: pending={statistics.Pending}, in-flight={statistics.InFlight}, dead-letter={statistics.DeadLetter}, spool-receipts={statistics.SpoolReceipts}, next-sequence={statistics.NextSequenceNumber}."));
        Console.WriteLine(FormattableString.Invariant(
            $"Spool status: ready={spool.Ready}, processing={spool.Processing}, accepted={spool.Accepted}, rejected={spool.Rejected}, temporary={spool.Temporary}."));
        Console.WriteLine(FormattableString.Invariant(
            $"Runtime gates: spool-import={snapshot.SpoolImportEnabled}, delivery={snapshot.DeliveryEnabled}, queue-capacity={snapshot.QueueCapacity}."));
        return 0;
    }

    private static HttpMessageHandler CreateHttpMessageHandler() =>
        new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.Brotli |
                DecompressionMethods.Deflate |
                DecompressionMethods.GZip,
            MaxConnectionsPerServer = 2,
            MaxResponseHeadersLength = 64,
            UseCookies = false
        };

    private static void PrintUsage()
    {
        Console.WriteLine("GoldSrcOps sandbox game-event agent");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  run");
        Console.WriteLine("  enqueue --file <event.json>");
        Console.WriteLine("  spool-write --file <event.json>");
        Console.WriteLine("  import-spool");
        Console.WriteLine("  verify-access-token");
        Console.WriteLine("  status [--json]");
        Console.WriteLine();
        Console.WriteLine("Delivery is disabled by default. Configure it through GameEventAgent__* environment variables.");
        Console.WriteLine("Supply only a path in GameEventAgent__Delivery__OAuth__ClientSecretFile; never put the secret itself in configuration.");
    }
}

internal static class GameEventAgentCommandLine
{
    public static bool TryParse(
        string[] args,
        out GameEventAgentCommand command,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0 ||
            (args.Length == 1 && string.Equals(args[0], "run", StringComparison.Ordinal)))
        {
            command = new RunAgentCommand();
            error = null;
            return true;
        }

        if (args.Length == 1 && string.Equals(args[0], "status", StringComparison.Ordinal))
        {
            command = new ShowQueueStatusCommand(Json: false);
            error = null;
            return true;
        }

        if (args.Length == 2 &&
            string.Equals(args[0], "status", StringComparison.Ordinal) &&
            string.Equals(args[1], "--json", StringComparison.Ordinal))
        {
            command = new ShowQueueStatusCommand(Json: true);
            error = null;
            return true;
        }

        if (args.Length == 1 && string.Equals(args[0], "import-spool", StringComparison.Ordinal))
        {
            command = new ImportSpoolCommand();
            error = null;
            return true;
        }

        if (args.Length == 1 && string.Equals(args[0], "verify-access-token", StringComparison.Ordinal))
        {
            command = new VerifyAccessTokenAgentCommand();
            error = null;
            return true;
        }

        if (args.Length == 3 &&
            (string.Equals(args[0], "enqueue", StringComparison.Ordinal) ||
                string.Equals(args[0], "spool-write", StringComparison.Ordinal)) &&
            string.Equals(args[1], "--file", StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(args[2]))
        {
            command = string.Equals(args[0], "enqueue", StringComparison.Ordinal)
                ? new EnqueueAgentCommand(args[2])
                : new WriteSpoolRecordCommand(args[2]);
            error = null;
            return true;
        }

        command = null!;
        error = "The command line is invalid.";
        return false;
    }
}

internal abstract record GameEventAgentCommand;

internal sealed record RunAgentCommand : GameEventAgentCommand;

internal sealed record EnqueueAgentCommand(string InputPath) : GameEventAgentCommand;

internal sealed record WriteSpoolRecordCommand(string InputPath) : GameEventAgentCommand;

internal sealed record ImportSpoolCommand : GameEventAgentCommand;

internal sealed record VerifyAccessTokenAgentCommand : GameEventAgentCommand;

internal sealed record ShowQueueStatusCommand(bool Json) : GameEventAgentCommand;
