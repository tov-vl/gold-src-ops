using System.Net;
using System.Text;
using GoldSrcOps.Application.Servers;
using GoldSrcOps.Infrastructure.A2S;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

return await ConsoleRunner.RunAsync(args);

internal static class ConsoleRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help", StringComparer.OrdinalIgnoreCase))
        {
            PrintUsage();
            return args.Length == 0 ? 2 : 0;
        }

        if (!QueryOptions.TryParse(args, out var options, out var error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine();
            PrintUsage();
            return 2;
        }

        using var cancellation = new CancellationTokenSource(options.Timeout);

        try
        {
            var client = new GoldSrcServerQueryClient(options.Encoding);
            var info = await client.QueryInfoAsync(
                new GameServerEndpoint(options.Host, options.Port, options.Timeout),
                cancellation.Token);

            Console.WriteLine($"Server:      {info.Name}");
            Console.WriteLine($"Endpoint:    {options.Host}:{options.Port}");
            Console.WriteLine($"Engine:      {info.ResponseFormat}");
            Console.WriteLine($"Map:         {info.Map}");
            Console.WriteLine($"Players:     {info.Players}/{info.MaxPlayers} ({info.Bots} bots)");
            Console.WriteLine($"Folder:      {info.Folder}");
            Console.WriteLine($"Game:        {info.Game}");
            Console.WriteLine($"Protocol:    {info.Protocol}");
            Console.WriteLine($"Type:        {info.ServerType}");
            Console.WriteLine($"Environment: {info.Environment}");
            Console.WriteLine($"Private:     {info.IsPrivate}");
            Console.WriteLine($"VAC:         {info.HasVac}");
            Console.WriteLine($"Version:     {info.Version ?? "unknown"}");
            Console.WriteLine($"Latency:     {info.Latency.TotalMilliseconds:0} ms");

            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine($"Timed out after {options.Timeout.TotalMilliseconds:0} ms.");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("GoldSrcOps A2S spike");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run --project src/GoldSrcOps.A2SSpike -- <host> <queryPort> [--timeout <ms>] [--encoding <name>]");
        Console.WriteLine("  dotnet run --project src/GoldSrcOps.A2SSpike -- <host:queryPort> [--timeout <ms>] [--encoding <name>]");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  dotnet run --project src/GoldSrcOps.A2SSpike -- 217.156.22.86 27015");
        Console.WriteLine("  dotnet run --project src/GoldSrcOps.A2SSpike -- server.csomod.com:27015 --encoding windows-1251");
    }
}

internal sealed record QueryOptions(
    string Host,
    int Port,
    TimeSpan Timeout,
    Encoding Encoding)
{
    public static bool TryParse(string[] args, out QueryOptions options, out string error)
    {
        options = default!;
        error = string.Empty;

        var positionals = new List<string>();
        var timeout = TimeSpan.FromSeconds(3);
        var encodingName = "utf-8";

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (arg.Equals("--timeout", StringComparison.OrdinalIgnoreCase))
            {
                if (++i >= args.Length || !int.TryParse(args[i], out var timeoutMs) || timeoutMs <= 0)
                {
                    error = "--timeout expects a positive integer value in milliseconds.";
                    return false;
                }

                timeout = TimeSpan.FromMilliseconds(timeoutMs);
                continue;
            }

            if (arg.Equals("--encoding", StringComparison.OrdinalIgnoreCase))
            {
                if (++i >= args.Length)
                {
                    error = "--encoding expects an encoding name, for example utf-8 or windows-1251.";
                    return false;
                }

                encodingName = args[i];
                continue;
            }

            positionals.Add(arg);
        }

        if (positionals.Count is < 1 or > 2)
        {
            error = "Expected <host> <queryPort> or <host:queryPort>.";
            return false;
        }

        string host;
        int port;

        if (positionals.Count == 1)
        {
            var endpoint = positionals[0];
            var separatorIndex = endpoint.LastIndexOf(':');

            if (separatorIndex <= 0 || separatorIndex == endpoint.Length - 1)
            {
                error = "Single-argument endpoint must use <host:queryPort> format.";
                return false;
            }

            host = endpoint[..separatorIndex];

            if (!int.TryParse(endpoint[(separatorIndex + 1)..], out port))
            {
                error = "queryPort must be an integer.";
                return false;
            }
        }
        else
        {
            host = positionals[0];

            if (!int.TryParse(positionals[1], out port))
            {
                error = "queryPort must be an integer.";
                return false;
            }
        }

        if (port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
        {
            error = $"queryPort must be between {IPEndPoint.MinPort} and {IPEndPoint.MaxPort}.";
            return false;
        }

        try
        {
            options = new QueryOptions(host, port, timeout, Encoding.GetEncoding(encodingName));
            return true;
        }
        catch (ArgumentException)
        {
            error = $"Unknown encoding '{encodingName}'.";
            return false;
        }
    }
}
