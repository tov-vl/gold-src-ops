using System.Security.Cryptography;
using System.Text.Json;
using GoldSrcOps.Domain.Servers;
using Microsoft.EntityFrameworkCore;

namespace GoldSrcOps.TelemetryAcceptance;

internal sealed class QueueRecoveryAcceptance
{
    private static readonly string[] SpoolDirectories = ["incoming", "processing", "accepted", "rejected"];
    private readonly AcceptanceApi _factory;
    private readonly HttpClient _api;
    private readonly string _readerToken;
    private readonly string _agent;
    private readonly string _scratch;
    private readonly string _secret;
    private readonly NativeInput[] _native;

    public QueueRecoveryAcceptance(AcceptanceApi factory, HttpClient api, string readerToken,
        string agent, string inputs, string scratch, string secret)
    {
        _factory = factory;
        _api = api;
        _readerToken = readerToken;
        _agent = agent;
        _scratch = scratch;
        _secret = secret;
        _native = [ReadInput(inputs, "ordinary"), ReadInput(inputs, "lost-ack"), ReadInput(inputs, "unavailable-restart")];
        EntryPoint.Require(_native.Select(static item => item.RecordId).Distinct().Take(4).Count() == 3 &&
            _native.Select(static item => item.OccurredAtUtc).Distinct().Take(4).Count() == 3, "Native objective identities or times are not distinct.");
    }

    public async Task<object> RunAsync(Guid serverId, FaultProxy proxy)
    {
        var capacity = await VerifyCapacityAsync(serverId, proxy).ConfigureAwait(false);
        var deadLetter = await VerifyDeadLetterAsync().ConfigureAwait(false);
        var count = await _factory.DatabaseAsync(context => context.GameEventInbox.CountAsync(CancellationToken.None)).ConfigureAwait(false);
        EntryPoint.Require(count == 5, "Expected three recovered and two non-rejected events.");
        return new
        {
            status = "native-telemetry-queue-recovery-accepted",
            nativeInputHashes = _native.ToDictionary(static item => item.Name, static item => item.Sha256, StringComparer.Ordinal),
            scenarios = new[] { capacity.Full, capacity.Drained, deadLetter },
            persistedRows = count,
        };
    }

    private async Task<(object Full, object Drained)> VerifyCapacityAsync(Guid serverId, FaultProxy proxy)
    {
        var directory = await InitializeAsync("capacity", serverId, proxy).ConfigureAwait(false);
        var initial = AgentProcess.ReadSnapshot(directory);
        proxy.Mode = DeliveryMode.Unavailable;
        QueueSnapshot? full = null;
        var maximumObservedDepth = 0;
        var worker = AgentProcess.StartCombined(_agent, directory, serverId, proxy.Address, capacity: 2);
        await using (worker.ConfigureAwait(false))
        {
            await EntryPoint.WaitAsync(() =>
            {
                var snapshot = Observe(directory, initial.Source, capacity: 2, ref maximumObservedDepth);
                var cycles = ReadCycles(directory);
                if (snapshot.Entries.Count != 2 || snapshot.SpoolReceipts != 0 || SpoolFiles(directory).Length != 1 ||
                    cycles is not { ImportOutcome: "blocked", BothCompleted: true } || !proxy.Observations.Any(static item => item.Status == 503))
                {
                    return false;
                }

                full = snapshot;
                return true;
            }, worker).ConfigureAwait(false);
        }

        var retained = AgentProcess.ReadSnapshot(directory);
        EntryPoint.Require(full is not null && retained.NextSequence == 3 &&
            retained.Entries.Select(static item => item.Identity).SequenceEqual(full.Entries.Select(static item => item.Identity)),
            "Stopping the full queue changed its identities or consumed another sequence.");
        var deferred = SpoolFiles(directory).Single();
        EntryPoint.Require(new FileInfo(deferred).Length <= 4096, "Deferred record exceeds native byte bound.");
        var deferredBytes = await File.ReadAllBytesAsync(deferred, CancellationToken.None).ConfigureAwait(false);
        var deferredHash = Convert.ToHexString(SHA256.HashData(deferredBytes));
        EntryPoint.Require(_native.Where(item => string.Equals(item.Sha256, deferredHash, StringComparison.Ordinal)).Take(2).Count() == 1,
            "Deferred spool bytes differ from native input.");
        var beforeRows = await _factory.DatabaseAsync(context => context.GameEventInbox.CountAsync(CancellationToken.None)).ConfigureAwait(false);
        EntryPoint.Require(beforeRows == 0 && proxy.Observations.All(static item => item.Status == 503), "Unavailable delivery reached persistence.");
        var previousCycles = ReadCycles(directory) ?? throw new InvalidOperationException("Missing worker cycles.");
        object fullResult = new
        {
            scenario = "bounded-capacity",
            status = "passed",
            serverId,
            capacity = 2,
            maximumObservedDepth,
            queued = retained.Entries.Count,
            deferred = 1,
            deferredSha256 = deferredHash,
            snapshot = retained,
            cycles = previousCycles,
            persistedRows = beforeRows,
        };

        proxy.Mode = DeliveryMode.Healthy;
        var restarted = AgentProcess.StartCombined(_agent, directory, serverId, proxy.Address, capacity: 2);
        await using (restarted.ConfigureAwait(false))
        {
            await EntryPoint.WaitAsync(() =>
            {
                var snapshot = Observe(directory, initial.Source, capacity: 2, ref maximumObservedDepth);
                return snapshot is { Entries.Count: 0, SpoolReceipts: 0, NextSequence: 4 } && SpoolFiles(directory).Length == 0 &&
                    ReadCycles(directory) is { ImportOutcome: "idle", DeliveryOutcome: "idle", BothCompleted: true } cycles &&
                    cycles.StartedAtUtc != previousCycles.StartedAtUtc;
            }, restarted).ConfigureAwait(false);
        }

        var observations = proxy.Observations;
        var accepted = observations.Where(static item => item.Receipt is not null).ToArray();
        await VerifyDeliveredAsync(serverId, retained.Source, accepted, expectedCount: 3).ConfigureAwait(false);
        EntryPoint.Require(accepted.Select(static item => item.Receipt!.SequenceNumber).Order().SequenceEqual([1L, 2L, 3L]),
            "Recovery lost or reused a source sequence.");
        foreach (var entry in retained.Entries)
        {
            EntryPoint.Require(accepted.Any(item => string.Equals(item.Receipt!.EventId.ToString("D"), entry.Identity.EventId, StringComparison.Ordinal)) &&
                observations.Where(item => string.Equals(item.Request!.EventId.ToString("D"), entry.Identity.EventId, StringComparison.Ordinal))
                .All(item => string.Equals(item.PayloadSha256, entry.Identity.PayloadSha256, StringComparison.Ordinal)),
                "Restart changed a queued HTTP payload.");
        }

        var final = AgentProcess.ReadSnapshot(directory);
        EntryPoint.Require(final.Entries.Count == 0 && final.NextSequence == 4 && string.Equals(final.Source, retained.Source, StringComparison.Ordinal),
            "Recovery changed the source state.");
        object drainedResult = new
        {
            scenario = "restart-and-drain",
            status = "passed",
            serverId,
            maximumObservedDepth,
            source = final.Source,
            nextSequence = final.NextSequence,
            queueDrained = true,
            spoolDrained = true,
            persistedRows = 3,
            readerProjection = "matched",
            restartedCycles = ReadCycles(directory),
            accepted,
        };
        return (fullResult, drainedResult);
    }

    private async Task<object> VerifyDeadLetterAsync()
    {
        var server = new Server("Synthetic dead-letter acceptance", GameServerKind.GoldSrc,
            new ServerEndpoint("127.0.0.1", queryPort: 27015, rconPort: null), pollIntervalSeconds: 30, notes: null,
            createdAtUtc: DateTimeOffset.UtcNow, isEnabled: false);
        await _factory.DatabaseAsync(async context =>
        {
            context.Servers.Add(server);
            return await context.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);
        var proxy = await FaultProxy.StartAsync(_api, server.Id, _factory.Token(server.Id, reader: false), _secret,
            rejectedOccurrence: _native[0].OccurredAtUtc).ConfigureAwait(false);
        await using var proxyLifetime = proxy.ConfigureAwait(false);
        var directory = await InitializeAsync("dead-letter", server.Id, proxy).ConfigureAwait(false);
        var initial = AgentProcess.ReadSnapshot(directory);
        var maximumObservedDepth = 0;
        var worker = AgentProcess.StartCombined(_agent, directory, server.Id, proxy.Address, capacity: 3);
        await using (worker.ConfigureAwait(false))
        {
            await EntryPoint.WaitAsync(() =>
            {
                var snapshot = Observe(directory, initial.Source, capacity: 3, ref maximumObservedDepth);
                return snapshot is { Entries: [{ Status: 2, Attempts: 1, Failure: "conflict" }], SpoolReceipts: 0, NextSequence: 4 } &&
                    SpoolFiles(directory).Length == 0 &&
                    ReadCycles(directory) is { ImportOutcome: "idle", DeliveryOutcome: "idle", BothCompleted: true };
            }, worker).ConfigureAwait(false);
        }

        var retained = AgentProcess.ReadSnapshot(directory);
        var previousCycles = ReadCycles(directory) ?? throw new InvalidOperationException("Missing worker cycles.");
        var rejected = proxy.Observations.Single(static item => item.Status == 409);
        EntryPoint.Require(string.Equals(retained.Entries[0].Identity.PayloadSha256, rejected.PayloadSha256, StringComparison.Ordinal),
            "Dead-letter evidence lost the rejected bytes.");
        var attempts = proxy.Observations.Length;
        var restarted = AgentProcess.StartCombined(_agent, directory, server.Id, proxy.Address, capacity: 3);
        await using (restarted.ConfigureAwait(false))
        {
            await EntryPoint.WaitAsync(() =>
                ReadCycles(directory) is { ImportOutcome: "idle", DeliveryOutcome: "idle", BothCompleted: true } cycles &&
                cycles.StartedAtUtc != previousCycles.StartedAtUtc, restarted).ConfigureAwait(false);
        }

        var final = AgentProcess.ReadSnapshot(directory);
        EntryPoint.Require(string.Equals(final.Source, retained.Source, StringComparison.Ordinal) && final.NextSequence == retained.NextSequence &&
            final.Entries.SequenceEqual(retained.Entries) && proxy.Observations.Length == attempts,
            "Restart changed or automatically replayed a dead letter.");
        var accepted = proxy.Observations.Where(static item => item.Receipt is not null).ToArray();
        await VerifyDeliveredAsync(server.Id, retained.Source, accepted, expectedCount: 2).ConfigureAwait(false);
        var rejectedId = rejected.Request!.EventId;
        var rejectedPersisted = await _factory.DatabaseAsync(context => context.GameEventInbox.AnyAsync(row => row.Id == rejectedId,
            CancellationToken.None)).ConfigureAwait(false);
        EntryPoint.Require(!rejectedPersisted, "Permanently rejected event entered the inbox.");
        return new
        {
            scenario = "dead-letter-isolation",
            status = "passed",
            serverId = server.Id,
            capacity = 3,
            maximumObservedDepth,
            snapshot = final,
            persistedRows = 2,
            deadLetterRetainedAcrossRestart = true,
            automaticReplay = false,
            previousCycles,
            restartedCycles = ReadCycles(directory),
            readerProjection = "matched",
            rejected,
            accepted,
        };
    }

    private async Task<string> InitializeAsync(string name, Guid serverId, FaultProxy proxy)
    {
        var directory = Path.Combine(_scratch, name);
        EntryPoint.Require(!Directory.Exists(directory), "Scenario directory must be fresh.");
        Directory.CreateDirectory(directory);
        var secretPath = Path.Combine(directory, "oauth-secret");
        await File.WriteAllTextAsync(secretPath, _secret, CancellationToken.None).ConfigureAwait(false);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(secretPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var initialize = AgentProcess.Start(_agent, directory, serverId, proxy.Address, delivery: false, "initialize-queue", "--fresh");
        await using (initialize.ConfigureAwait(false))
        {
            await initialize.CompleteAsync().ConfigureAwait(false);
        }

        var incoming = Path.Combine(directory, "spool", "incoming");
        Directory.CreateDirectory(incoming);
        foreach (var input in _native)
        {
            await File.WriteAllBytesAsync(Path.Combine(incoming, input.RecordId.ToString("D") + ".json"), input.Bytes,
                CancellationToken.None).ConfigureAwait(false);
        }

        return directory;
    }

    private async Task VerifyDeliveredAsync(Guid serverId, string source, DeliveryObservation[] observations, int expectedCount)
    {
        EntryPoint.Require(observations.Length == expectedCount && observations.All(static item => item is { Status: 202, Receipt.Duplicate: false }),
            "Expected exactly one fresh acceptance for every deliverable native record.");
        EntryPoint.Require(observations.Select(static item => item.Receipt!.EventId).Distinct().Take(expectedCount + 1).Count() == expectedCount,
            "Duplicate inbox identity in delivery evidence.");
        EntryPoint.Require(observations.All(item => string.Equals(item.Receipt!.SourceInstanceId.ToString("D"), source, StringComparison.Ordinal)),
            "Delivery replaced the retained source identity.");
        foreach (var observation in observations)
        {
            var request = observation.Request!;
            var native = _native.Single(item => item.OccurredAtUtc == request.OccurredAtUtc);
            EntryPoint.Require(string.Equals(request.Type, native.Event.GetProperty("type").GetString(), StringComparison.Ordinal) &&
                string.Equals(request.Map, native.Event.GetProperty("map").GetString(), StringComparison.Ordinal) &&
                request.Players == native.Event.GetProperty("players").GetInt32() && request.Bots == native.Event.GetProperty("bots").GetInt32(),
                "Delivered HTTP fields differ from native input.");
            await EntryPoint.VerifyPersistenceAsync(_factory, _api, _readerToken, serverId, observation.Receipt!, native.Event, expectedCount).ConfigureAwait(false);
        }
    }

    private static QueueSnapshot Observe(string directory, string source, int capacity, ref int maximumDepth)
    {
        var snapshot = AgentProcess.ReadSnapshot(directory);
        maximumDepth = Math.Max(maximumDepth, snapshot.Entries.Count);
        EntryPoint.Require(snapshot.Entries.Count <= capacity && string.Equals(snapshot.Source, source, StringComparison.Ordinal) && snapshot.NextSequence is >= 1 and <= 4,
            "Queue capacity or source/sequence boundary was violated.");
        return snapshot;
    }

    private static string[] SpoolFiles(string directory)
    {
        var directories = SpoolDirectories.Select(name => Path.Combine(directory, "spool", name)).Where(Directory.Exists);
        return [.. directories.SelectMany(Directory.GetFiles)];
    }

    private static WorkerCycles? ReadCycles(string directory)
    {
        try
        {
            using var stream = new FileStream(Path.Combine(directory, "queue.db.health.json"), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            EntryPoint.Require(stream.Length is > 0 and <= 16 * 1024, "Worker cycle record exceeds bound.");
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            EntryPoint.Require(root.GetProperty("schemaVersion").GetInt32() == 1 && root.GetProperty("importEnabled").GetBoolean() &&
                root.GetProperty("deliveryEnabled").GetBoolean(), "Both real worker loops must be enabled.");
            var started = root.GetProperty("startedAtUtc").GetDateTimeOffset();
            var import = root.GetProperty("import");
            var delivery = root.GetProperty("delivery");
            var complete = import.GetProperty("lastCompletedAtUtc");
            var delivered = delivery.GetProperty("lastCompletedAtUtc");
            var importTime = complete.ValueKind == JsonValueKind.Null ? (DateTimeOffset?)null : complete.GetDateTimeOffset();
            var deliveryTime = delivered.ValueKind == JsonValueKind.Null ? (DateTimeOffset?)null : delivered.GetDateTimeOffset();
            return new WorkerCycles(started, import.GetProperty("outcome").GetString(), delivery.GetProperty("outcome").GetString(),
                importTime, deliveryTime);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private static NativeInput ReadInput(string directory, string name)
    {
        var bytes = File.ReadAllBytes(Path.Combine(directory, name + ".json"));
        EntryPoint.Require(bytes.Length is > 0 and <= 4096, "Native input exceeds bound.");
        using var document = JsonDocument.Parse(bytes);
        return new NativeInput(name, document.RootElement.GetProperty("recordId").GetGuid(), bytes,
            Convert.ToHexString(SHA256.HashData(bytes)), document.RootElement.GetProperty("event").Clone());
    }
}
