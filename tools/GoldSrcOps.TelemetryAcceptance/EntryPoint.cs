using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using GoldSrcOps.Contracts.GameEvents;
using GoldSrcOps.Domain.GameEvents;
using GoldSrcOps.Domain.Servers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace GoldSrcOps.TelemetryAcceptance;

internal static class EntryPoint
{
    private static readonly JsonSerializerOptions ResultJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        Require(args.Length == 5 || (args.Length == 6 && (string.Equals(args[5], "queue-recovery", StringComparison.Ordinal) || string.Equals(args[5], "server-isolation", StringComparison.Ordinal))),
            "Expected repository, agent, inputs, scratch and result paths, optionally queue-recovery or server-isolation.");
        var repository = Path.GetFullPath(args[0]);
        var agent = Path.GetFullPath(args[1]);
        var inputs = Path.GetFullPath(args[2]);
        var scratch = Path.GetFullPath(args[3]);
        var resultPath = Path.GetFullPath(args[4]);
        Require(!Directory.Exists(scratch) && !File.Exists(resultPath), "Acceptance output must be fresh.");
        Directory.CreateDirectory(scratch);
        var database = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("telemetry_acceptance")
            .WithUsername("acceptance")
            .WithPassword(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)))
            .WithCreateParameterModifier(parameters =>
            {
                var hostConfig = parameters.HostConfig ?? throw new InvalidOperationException("Missing PostgreSQL host configuration.");
                var portBindings = hostConfig.PortBindings ?? throw new InvalidOperationException("Missing PostgreSQL port binding.");
                foreach (var bindings in portBindings.Values)
                {
                    foreach (var binding in bindings)
                    {
                        binding.HostIP = "127.0.0.1";
                    }
                }

                hostConfig.Memory = 512 * 1024 * 1024;
                hostConfig.NanoCPUs = 1_000_000_000;
            })
            .Build();
        await using var databaseLifetime = database.ConfigureAwait(false);
        using var startupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var startupToken = startupDeadline.Token;
        await database.StartAsync(startupToken).ConfigureAwait(false);
        Require(string.Equals(database.Hostname, "localhost", StringComparison.OrdinalIgnoreCase) ||
            (IPAddress.TryParse(database.Hostname, out var hostAddress) && IPAddress.IsLoopback(hostAddress)),
            "Acceptance requires a local Docker host.");
        var factory = new AcceptanceApi(repository, database.GetConnectionString());
        await using var factoryLifetime = factory.ConfigureAwait(false);
        using var api = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = factory.Address });
        api.Timeout = TimeSpan.FromSeconds(5);
        var server = new Server("Synthetic telemetry acceptance", GameServerKind.GoldSrc,
            new ServerEndpoint("127.0.0.1", queryPort: 27015, rconPort: null), pollIntervalSeconds: 30, notes: null, createdAtUtc: DateTimeOffset.UtcNow, isEnabled: false);
        await factory.DatabaseAsync(async context =>
        {
            await context.Database.MigrateAsync(startupToken).ConfigureAwait(false);
            context.Servers.Add(server);
            return await context.SaveChangesAsync(startupToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
        var writerToken = factory.Token(server.Id, reader: false);
        var readerToken = factory.Token(server.Id, reader: true);
        await VerifyAuthorizationAsync(api, server.Id, writerToken, readerToken).ConfigureAwait(false);
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var proxy = await FaultProxy.StartAsync(api, server.Id, writerToken, secret).ConfigureAwait(false);
        await using var proxyLifetime = proxy.ConfigureAwait(false);
        switch (args.Length == 6 ? args[5] : null)
        {
            case "server-isolation":
                {
                    var isolation = new ServerIsolationAcceptance(factory, api, readerToken, agent, inputs, scratch);
                    var result = await isolation.RunAsync(server.Id, proxy, secret, writerToken).ConfigureAwait(false);
                    await WriteResultAsync(resultPath, result).ConfigureAwait(false);
                    Console.WriteLine("NATIVE_TELEMETRY_SERVER_ISOLATION=passed scenarios=4 rows=6");
                    return 0;
                }
            case "queue-recovery":
                {
                    var acceptance = new QueueRecoveryAcceptance(factory, api, readerToken, agent, inputs, scratch, secret);
                    var result = await acceptance.RunAsync(server.Id, proxy).ConfigureAwait(false);
                    await WriteResultAsync(resultPath, result).ConfigureAwait(false);
                    Console.WriteLine("NATIVE_TELEMETRY_QUEUE_RECOVERY=passed scenarios=3 rows=5");
                    return 0;
                }
        }
        var results = new List<object>();
        var cases = new[] { "ordinary", "lost-ack", "unavailable-restart" };
        for (var index = 0; index < cases.Length; index++)
        {
            var name = cases[index];
            var directory = Path.Combine(scratch, name);
            Directory.CreateDirectory(directory);
            var secretPath = Path.Combine(directory, "oauth-secret");
            await File.WriteAllTextAsync(secretPath, secret, CancellationToken.None).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(secretPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            var raw = await File.ReadAllBytesAsync(Path.Combine(inputs, name + ".json"), CancellationToken.None).ConfigureAwait(false);
            using var source = JsonDocument.Parse(raw);
            var incoming = Path.Combine(directory, "spool", "incoming");
            Directory.CreateDirectory(incoming);
            var initialize = AgentProcess.Start(agent, directory, server.Id, proxy.Address, delivery: false, "initialize-queue", "--fresh");
            await using (initialize.ConfigureAwait(false))
            {
                await initialize.CompleteAsync().ConfigureAwait(false);
            }

            await File.WriteAllBytesAsync(Path.Combine(incoming, source.RootElement.GetProperty("recordId").GetString() + ".json"), raw, CancellationToken.None).ConfigureAwait(false);
            var import = AgentProcess.Start(agent, directory, server.Id, proxy.Address, delivery: false, "import-spool");
            await using (import.ConfigureAwait(false))
            {
                await import.CompleteAsync().ConfigureAwait(false);
            }

            var before = AgentProcess.ReadQueue(directory) ?? throw new InvalidOperationException("Native record was not imported.");
            var observationStart = proxy.Observations.Length;
            proxy.Mode = index switch { 1 => DeliveryMode.LoseAcknowledgement, 2 => DeliveryMode.Unavailable, _ => DeliveryMode.Healthy };
            var first = AgentProcess.Start(agent, directory, server.Id, proxy.Address, delivery: true, "run");
            await using (first.ConfigureAwait(false))
            {
                if (index == 0)
                {
                    await WaitAsync(() => AgentProcess.ReadQueue(directory) is null, first).ConfigureAwait(false);
                }
                else
                {
                    await WaitAsync(() => proxy.Observations.Length > observationStart, first).ConfigureAwait(false);
                    var committed = await factory.DatabaseAsync(context => context.GameEventInbox.CountAsync(CancellationToken.None)).ConfigureAwait(false);
                    Require(committed == 2, "Fault injection did not reach the expected commit boundary.");
                }
            }

            if (index != 0)
            {
                Require(AgentProcess.ReadQueue(directory) == before, "Agent restart changed event identity, sequence or payload.");
                proxy.Mode = DeliveryMode.Healthy;
                var restarted = AgentProcess.Start(agent, directory, server.Id, proxy.Address, delivery: true, "run");
                await using var restartedLifetime = restarted.ConfigureAwait(false);
                await WaitAsync(() => AgentProcess.ReadQueue(directory) is null, restarted).ConfigureAwait(false);
            }

            var observations = proxy.Observations[observationStart..];
            Require(AgentProcess.ReadState(directory) == (before.Source, before.NextSequence), "Delivery changed the retained source or next sequence.");
            Require(observations.Length >= (index == 0 ? 1 : 2) &&
                observations.All(item => string.Equals(item.PayloadSha256, before.PayloadSha256, StringComparison.Ordinal)), "Retry changed the exact HTTP payload.");
            var accepted = observations.Where(static item => item.Receipt is not null).ToArray();
            Require(accepted.Length == (index == 1 ? 2 : 1) && accepted[0].Status == 202 && !accepted[0].Receipt!.Duplicate,
                "Expected exactly one fresh acceptance.");
            switch (index)
            {
                case 1:
                    Require(accepted[1].Status == 200 && accepted[1].Receipt!.Duplicate &&
                        accepted[1].Receipt == accepted[0].Receipt! with { Duplicate = true }, "Replay did not return the original receipt.");
                    break;
                case 2:
                    Require(observations[0].Status == 503, "Unavailable scenario was not exercised.");
                    break;
            }

            var receipt = accepted[0].Receipt!;
            Require(string.Equals(receipt.EventId.ToString("D"), before.EventId, StringComparison.Ordinal) &&
                string.Equals(receipt.SourceInstanceId.ToString("D"), before.Source, StringComparison.Ordinal) &&
                receipt.SequenceNumber == before.Sequence && receipt.ServerId == server.Id, "Receipt identity differs from the imported event.");
            await VerifyPersistenceAsync(factory, api, readerToken, server.Id, receipt, source.RootElement.GetProperty("event"), index + 1).ConfigureAwait(false);
            results.Add(new
            {
                scenario = name,
                status = "passed",
                nativeSha256 = Convert.ToHexString(SHA256.HashData(raw)),
                identity = before,
                receipt,
                statuses = observations.Select(static item => item.Status).ToArray(),
                queueDrained = true,
                persistedRows = 1,
                readerProjection = "matched",
            });
        }

        await WriteResultAsync(resultPath, new
        {
            status = "native-telemetry-e2e-accepted",
            transport = "loopback HTTP with real JWT validation",
            persistence = "disposable PostgreSQL with production migrations",
            scenarios = results,
        }).ConfigureAwait(false);
        Console.WriteLine("NATIVE_TELEMETRY_E2E=passed scenarios=3 rows=3");
        return 0;
    }

    public static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static Task WriteResultAsync(string path, object result) =>
        File.WriteAllTextAsync(path, JsonSerializer.Serialize(result, ResultJson) + Environment.NewLine, CancellationToken.None);

    internal static async Task WaitAsync(Func<bool> predicate, AgentProcess process)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        while (!predicate())
        {
            process.RequireRunning();
            await Task.Delay(100, deadline.Token).ConfigureAwait(false);
        }

        process.RequireRunning();
    }

    private static async Task VerifyAuthorizationAsync(HttpClient api, Guid serverId, string writer, string reader)
    {
        using var read = new HttpRequestMessage(HttpMethod.Get, $"/api/servers/{serverId:D}/game-events");
        read.Headers.Authorization = new AuthenticationHeaderValue("Bearer", writer);
        using var readResponse = await api.SendAsync(read).ConfigureAwait(false);
        Require(readResponse.StatusCode == HttpStatusCode.Forbidden, "Machine token gained human Reader access.");
        using var write = new HttpRequestMessage(HttpMethod.Post, $"/api/servers/{serverId:D}/game-events");
        write.Content = JsonContent.Create(new { });
        write.Headers.Authorization = new AuthenticationHeaderValue("Bearer", reader);
        using var writeResponse = await api.SendAsync(write).ConfigureAwait(false);
        Require(writeResponse.StatusCode == HttpStatusCode.Forbidden, "Reader token gained ingest access.");
    }

    internal static async Task VerifyPersistenceAsync(AcceptanceApi factory, HttpClient api, string reader, Guid serverId,
        GameEventIngestResponse receipt, JsonElement native, int expectedCount)
    {
        var rows = await factory.DatabaseAsync(context => context.GameEventInbox.AsNoTracking()
            .Where(row => row.ServerId == serverId).ToListAsync()).ConfigureAwait(false);
        Require(rows.Count == expectedCount && rows.Where(row => row.Id == receipt.EventId).Take(2).Count() == 1, "Inbox contains a missing or duplicate row.");
        var row = rows.Single(row => row.Id == receipt.EventId);
        Require(row.ServerId == serverId && row.SourceInstanceId == receipt.SourceInstanceId && row.SequenceNumber == receipt.SequenceNumber &&
            row.ReceivedAtUtc == receipt.ReceivedAtUtc && row.OccurredAtUtc == native.GetProperty("occurredAtUtc").GetDateTimeOffset() &&
            row is { Type: GameEventType.RoundEnded, ContractVersion: 1 } &&
            string.Equals(row.Map, native.GetProperty("map").GetString(), StringComparison.Ordinal) && row.Players == native.GetProperty("players").GetInt32() &&
            row.Bots == native.GetProperty("bots").GetInt32(), "PostgreSQL row differs from native telemetry.");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/servers/{serverId:D}/game-events?limit=10");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", reader);
        using var response = await api.SendAsync(request).ConfigureAwait(false);
        Require(response.StatusCode == HttpStatusCode.OK, "Reader projection failed.");
        var history = await response.Content.ReadFromJsonAsync<GameEventHistoryResponse>().ConfigureAwait(false)
            ?? throw new InvalidOperationException("Reader response is empty.");
        Require(history.ServerId == serverId && history.Limit == 10 && history.Items.Count == expectedCount &&
            history.Items.Where(item => item.OccurredAtUtc == row.OccurredAtUtc && string.Equals(item.Type, "round.ended", StringComparison.Ordinal) &&
                string.Equals(item.Map, row.Map, StringComparison.Ordinal) && item.Players == row.Players && item.Bots == row.Bots).Take(2).Count() == 1,
            "Reader projection lost native event fields or duplicated an event.");
    }
}
