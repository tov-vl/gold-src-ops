using System.Net;
using System.Text.Json;
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
                ShowQueueStatusCommand => ShowStatus(options.Queue),
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

    private static async Task<int> RunWorkerAsync(
        HostApplicationBuilder builder,
        GameEventAgentOptions options,
        CancellationToken cancellationToken)
    {
        var delivery = options.Delivery ?? throw new InvalidOperationException(
            "Game-event delivery is disabled. Set GameEventAgent:Delivery:Enabled to true after supplying the complete sandbox configuration.");

        builder.Services.AddSingleton(options.Queue);
        builder.Services.AddSingleton(delivery);
        builder.Services.AddSingleton(delivery.OAuth);
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddSingleton<IGameEventOutbox, SqliteGameEventOutbox>();
        builder.Services.AddSingleton<IGameEventAccessTokenProvider, ClientCredentialsAccessTokenProvider>();
        builder.Services.AddSingleton<IGameEventDeliveryClient, GameEventDeliveryClient>();
        builder.Services.AddSingleton<IRetryDelayPolicy, ExponentialRetryDelayPolicy>();
        builder.Services.AddSingleton<GameEventDispatcher>();
        builder.Services.AddHostedService<GameEventDeliveryWorker>();

        builder.Services
            .AddHttpClient(
                ClientCredentialsAccessTokenProvider.HttpClientName,
                client => client.Timeout = delivery.RequestTimeout)
            .ConfigurePrimaryHttpMessageHandler(CreateHttpMessageHandler);
        builder.Services
            .AddHttpClient(
                GameEventDeliveryClient.HttpClientName,
                client =>
                {
                    client.BaseAddress = delivery.ApiBaseUri;
                    client.Timeout = delivery.RequestTimeout;
                })
            .ConfigurePrimaryHttpMessageHandler(CreateHttpMessageHandler);

        using var host = builder.Build();
        await host.RunAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> EnqueueAsync(
        GameEventQueueOptions queueOptions,
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
        var input = await JsonSerializer.DeserializeAsync<GameEventSourceInput>(
            stream,
            GameEventJson.StrictSerializerOptions,
            cancellationToken).ConfigureAwait(false) ??
            throw new InvalidOperationException("The test-source input file contains no event.");

        var outbox = new SqliteGameEventOutbox(queueOptions);
        outbox.Initialize();
        var queued = outbox.Enqueue(input, TimeProvider.System.GetUtcNow());
        Console.WriteLine(FormattableString.Invariant(
            $"Event queued: sequence {queued.SequenceNumber}, event {queued.EventId:D}."));
        return 0;
    }

    private static int ShowStatus(GameEventQueueOptions queueOptions)
    {
        var outbox = new SqliteGameEventOutbox(queueOptions);
        outbox.Initialize();
        var statistics = outbox.GetStatistics();
        Console.WriteLine(FormattableString.Invariant(
            $"Queue status: pending={statistics.Pending}, in-flight={statistics.InFlight}, dead-letter={statistics.DeadLetter}, next-sequence={statistics.NextSequenceNumber}."));
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
        Console.WriteLine("  status");
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
            command = new ShowQueueStatusCommand();
            error = null;
            return true;
        }

        if (args.Length == 3 &&
            string.Equals(args[0], "enqueue", StringComparison.Ordinal) &&
            string.Equals(args[1], "--file", StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(args[2]))
        {
            command = new EnqueueAgentCommand(args[2]);
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

internal sealed record ShowQueueStatusCommand : GameEventAgentCommand;
