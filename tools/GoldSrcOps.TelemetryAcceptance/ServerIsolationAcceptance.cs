using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using GoldSrcOps.Domain.Servers;
using Microsoft.EntityFrameworkCore;

namespace GoldSrcOps.TelemetryAcceptance;

internal sealed class ServerIsolationAcceptance
{
    private readonly AcceptanceApi _factory;
    private readonly HttpClient _api;
    private readonly string _reader;
    private readonly string _agent;
    private readonly string _scratch;
    private readonly NativeInput[] _inputs;

    public ServerIsolationAcceptance(AcceptanceApi factory, HttpClient api, string reader, string agent, string inputs, string scratch)
    {
        _factory = factory;
        _api = api;
        _reader = reader;
        _agent = agent;
        _scratch = scratch;
        _inputs = [AcceptanceFiles.ReadInput(inputs, "ordinary"), AcceptanceFiles.ReadInput(inputs, "lost-ack"),
            AcceptanceFiles.ReadInput(inputs, "unavailable-restart"),];
        EntryPoint.Require(_inputs.Select(static input => input.RecordId).Distinct().Take(4).Count() == 3 &&
            _inputs.Select(static input => input.OccurredAtUtc).Distinct().Take(4).Count() == 3, "Native identities and times must be distinct.");
    }

    public async Task<object> RunAsync(Guid firstId, FaultProxy firstProxy, string firstSecret, string firstToken)
    {
        var secondServer = new Server("Synthetic parallel telemetry", GameServerKind.GoldSrc,
            new ServerEndpoint("127.0.0.1", queryPort: 27016, rconPort: null), 30, notes: null,
            createdAtUtc: DateTimeOffset.UtcNow, isEnabled: false);
        await _factory.DatabaseAsync(async context =>
        {
            context.Servers.Add(secondServer);
            return await context.SaveChangesAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);
        var secondId = secondServer.Id;
        var secondSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var secondProxy = await FaultProxy.StartAsync(_api, secondId, _factory.Token(secondId, reader: false), secondSecret).ConfigureAwait(false);
        await using var secondProxyLifetime = secondProxy.ConfigureAwait(false);
        var firstDirectory = Path.Combine(_scratch, "first");
        var secondDirectory = Path.Combine(_scratch, "second");
        await AcceptanceFiles.PrepareAsync(_agent, firstDirectory, firstId, firstProxy, firstSecret, []).ConfigureAwait(false);
        await AcceptanceFiles.PrepareAsync(_agent, secondDirectory, secondId, secondProxy, secondSecret, []).ConfigureAwait(false);
        var firstState = AgentProcess.ReadState(firstDirectory);
        var secondState = AgentProcess.ReadState(secondDirectory);
        EntryPoint.Require(!string.Equals(firstState.Source, secondState.Source, StringComparison.Ordinal), "Servers share a source identity.");

        firstProxy.Mode = secondProxy.Mode = DeliveryMode.Unavailable;
        var first = AgentProcess.StartCombined(_agent, firstDirectory, firstId, firstProxy.Address, capacity: 3);
        await using var firstLifetime = first.ConfigureAwait(false);
        var second = AgentProcess.StartCombined(_agent, secondDirectory, secondId, secondProxy.Address, capacity: 3);
        await using var secondLifetime = second.ConfigureAwait(false);
        await WriteInputAsync(firstDirectory, _inputs[0]).ConfigureAwait(false);
        await WriteInputAsync(secondDirectory, _inputs[1]).ConfigureAwait(false);
        await WaitBothAsync(() => HasQueued(firstDirectory, 1) && HasQueued(secondDirectory, 1) &&
            firstProxy.Observations.Any(static item => item.Status == 503) && secondProxy.Observations.Any(static item => item.Status == 503),
            first, second).ConfigureAwait(false);
        var firstInitial = AgentProcess.ReadSnapshot(firstDirectory);
        var secondInitial = AgentProcess.ReadSnapshot(secondDirectory);
        firstProxy.Mode = secondProxy.Mode = DeliveryMode.Healthy;
        await WaitBothAsync(() => Drained(firstDirectory, firstProxy, 1) && Drained(secondDirectory, secondProxy, 1), first, second).ConfigureAwait(false);
        await VerifyDeliveredAsync(firstId, firstState.Source, firstProxy, 1).ConfigureAwait(false);
        await VerifyDeliveredAsync(secondId, secondState.Source, secondProxy, 1).ConfigureAwait(false);
        var parallel = new
        {
            scenario = "parallel-delivery",
            status = "passed",
            bothAgentsRunning = true,
            firstServerId = firstId,
            secondServerId = secondId,
            firstInitial,
            secondInitial,
            firstCycles = AcceptanceFiles.ReadCycles(firstDirectory),
            secondCycles = AcceptanceFiles.ReadCycles(secondDirectory),
            firstAccepted = Accepted(firstProxy),
            secondAccepted = Accepted(secondProxy),
            persistedRows = 2,
        };

        firstProxy.Mode = DeliveryMode.Unavailable;
        var failureStart = firstProxy.Observations.Length;
        await WriteInputAsync(firstDirectory, _inputs[1]).ConfigureAwait(false);
        await WriteInputAsync(secondDirectory, _inputs[2]).ConfigureAwait(false);
        await WaitBothAsync(() => HasQueued(firstDirectory, 1) && Drained(secondDirectory, secondProxy, 2) &&
            firstProxy.Observations.Skip(failureStart).Any(static item => item.Status == 503), first, second).ConfigureAwait(false);
        EntryPoint.Require(firstProxy.Mode == DeliveryMode.Unavailable, "Recovery was released before the healthy server progressed.");
        await VerifyDeliveredAsync(firstId, firstState.Source, firstProxy, 1).ConfigureAwait(false);
        await VerifyDeliveredAsync(secondId, secondState.Source, secondProxy, 2).ConfigureAwait(false);
        var failedSnapshot = AgentProcess.ReadSnapshot(firstDirectory);
        var failedRequest = firstProxy.Observations.Skip(failureStart).First(static item => item.Status == 503);
        var outage = new
        {
            scenario = "one-server-unavailable",
            status = "passed",
            bothAgentsRunning = true,
            firstServerId = firstId,
            secondServerId = secondId,
            recoveryReleased = false,
            firstSnapshot = failedSnapshot,
            rejectedDelivery = failedRequest,
            firstPersistedRows = 1,
            secondPersistedRows = 2,
            secondAccepted = Accepted(secondProxy),
        };

        var beforeForbidden = await CountRowsAsync().ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/servers/{secondId:D}/game-events");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", firstToken);
        request.Content = JsonContent.Create(failedRequest.Request!);
        using var response = await _api.SendAsync(request).ConfigureAwait(false);
        EntryPoint.Require(response.StatusCode == HttpStatusCode.Forbidden, "A server token gained cross-server ingest access.");
        var afterForbidden = await CountRowsAsync().ConfigureAwait(false);
        EntryPoint.Require(beforeForbidden == 3 && afterForbidden == beforeForbidden, "Forbidden ingest changed the inbox.");
        await WriteInputAsync(firstDirectory, _inputs[2]).ConfigureAwait(false);
        await WriteInputAsync(secondDirectory, _inputs[0]).ConfigureAwait(false);
        await WaitBothAsync(() => HasQueued(firstDirectory, 2) && Drained(secondDirectory, secondProxy, 3), first, second).ConfigureAwait(false);
        await VerifyDeliveredAsync(secondId, secondState.Source, secondProxy, 3).ConfigureAwait(false);
        EntryPoint.Require(firstProxy.Mode == DeliveryMode.Unavailable && await CountRowsAsync().ConfigureAwait(false) == 4,
            "The failed server recovered before the post-denial progress checkpoint.");
        var beforeRecovery = AgentProcess.ReadSnapshot(firstDirectory);
        var forbidden = new
        {
            scenario = "cross-server-forbidden",
            status = "passed",
            bothAgentsRunning = true,
            credentialServerId = firstId,
            targetServerId = secondId,
            httpStatus = (int)response.StatusCode,
            beforeRows = beforeForbidden,
            afterRows = afterForbidden,
            attempted = failedRequest,
            secondPersistedRowsAfterDenial = 3,
            recoveryReleased = false,
            beforeRecovery,
            secondAcceptedAfterDenial = Accepted(secondProxy),
        };

        firstProxy.Mode = DeliveryMode.Healthy;
        await WaitBothAsync(() => Drained(firstDirectory, firstProxy, 3) && Drained(secondDirectory, secondProxy, 3), first, second).ConfigureAwait(false);
        await VerifyDeliveredAsync(firstId, firstState.Source, firstProxy, 3).ConfigureAwait(false);
        await VerifyDeliveredAsync(secondId, secondState.Source, secondProxy, 3).ConfigureAwait(false);
        var firstFinal = AgentProcess.ReadSnapshot(firstDirectory);
        var secondFinal = AgentProcess.ReadSnapshot(secondDirectory);
        EntryPoint.Require(firstFinal.NextSequence == 4 && secondFinal.NextSequence == 4 &&
            string.Equals(firstFinal.Source, firstState.Source, StringComparison.Ordinal) &&
            string.Equals(secondFinal.Source, secondState.Source, StringComparison.Ordinal), "Recovery replaced source or next sequence.");
        var accepted = Accepted(firstProxy).Concat(Accepted(secondProxy)).ToArray();
        EntryPoint.Require(await CountRowsAsync().ConfigureAwait(false) == 6 &&
            accepted.Select(static item => item.Receipt!.EventId).Distinct().Take(7).Count() == 6,
            "Parallel recovery duplicated or mixed accepted identities.");
        foreach (var queued in firstInitial.Entries.Concat(failedSnapshot.Entries).Concat(beforeRecovery.Entries).Concat(secondInitial.Entries))
        {
            var delivered = accepted.Single(item => string.Equals(item.Request!.EventId.ToString("D"), queued.Identity.EventId, StringComparison.Ordinal));
            EntryPoint.Require(string.Equals(delivered.PayloadSha256, queued.Identity.PayloadSha256, StringComparison.Ordinal) &&
                delivered.Request!.SequenceNumber == queued.Identity.Sequence, "Delivery replaced queued bytes or sequence.");
        }

        first.RequireRunning();
        second.RequireRunning();
        var recovered = new
        {
            scenario = "recover-and-isolate",
            status = "passed",
            bothAgentsRunning = true,
            firstServerId = firstId,
            secondServerId = secondId,
            firstFinal,
            secondFinal,
            firstAccepted = Accepted(firstProxy),
            secondAccepted = Accepted(secondProxy),
            firstCycles = AcceptanceFiles.ReadCycles(firstDirectory),
            secondCycles = AcceptanceFiles.ReadCycles(secondDirectory),
            persistedRows = 6,
            readerProjection = "matched-per-server",
        };
        return new
        {
            status = "native-telemetry-server-isolation-accepted",
            persistedRows = 6,
            nativeInputHashes = _inputs.ToDictionary(static input => input.Name, static input => input.Sha256, StringComparer.Ordinal),
            scenarios = new object[] { parallel, outage, forbidden, recovered },
        };
    }

    private Task<int> CountRowsAsync() => _factory.DatabaseAsync(context => context.GameEventInbox.CountAsync());

    private async Task VerifyDeliveredAsync(Guid serverId, string source, FaultProxy proxy, int count)
    {
        var observations = Accepted(proxy);
        EntryPoint.Require(observations.Length == count && observations.All(item => item is { Status: 202, Receipt.Duplicate: false } &&
            item.Receipt.ServerId == serverId && string.Equals(item.Receipt.SourceInstanceId.ToString("D"), source, StringComparison.Ordinal)) &&
            observations.Select(static item => item.Receipt!.SequenceNumber).Order().SequenceEqual(Enumerable.Range(1, count).Select(static value => (long)value)),
            "Server identity, independent sequence or fresh receipt is invalid.");
        foreach (var observation in observations)
        {
            var input = _inputs.Single(input => input.OccurredAtUtc == observation.Request!.OccurredAtUtc);
            await EntryPoint.VerifyPersistenceAsync(_factory, _api, _reader, serverId, observation.Receipt!, input.Event, count).ConfigureAwait(false);
        }
    }

    private static DeliveryObservation[] Accepted(FaultProxy proxy) => [.. proxy.Observations.Where(static item => item.Receipt is not null)];

    private static bool HasQueued(string directory, int count) => AgentProcess.ReadSnapshot(directory).Entries.Count == count &&
        AcceptanceFiles.ReadCycles(directory) is { BothCompleted: true };

    private static bool Drained(string directory, FaultProxy proxy, int count) =>
        AgentProcess.ReadSnapshot(directory) is { Entries.Count: 0, SpoolReceipts: 0 } &&
        AcceptanceFiles.SpoolFiles(directory).Length == 0 && Accepted(proxy).Length == count &&
        AcceptanceFiles.ReadCycles(directory) is { BothCompleted: true, ImportOutcome: "idle", DeliveryOutcome: "idle" };

    private static Task WaitBothAsync(Func<bool> predicate, AgentProcess first, AgentProcess second) => EntryPoint.WaitAsync(() =>
    {
        second.RequireRunning();
        return predicate();
    }, first);

    private static async Task WriteInputAsync(string directory, NativeInput input)
    {
        var staged = Path.Combine(directory, input.RecordId.ToString("D") + ".tmp");
        await File.WriteAllBytesAsync(staged, input.Bytes, CancellationToken.None).ConfigureAwait(false);
        File.Move(staged, Path.Combine(directory, "spool", "incoming", input.RecordId.ToString("D") + ".json"), overwrite: false);
    }
}
