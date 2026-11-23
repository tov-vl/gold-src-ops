using System.Security.Cryptography;
using GoldSrcOps.Contracts.GameEvents;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

namespace GoldSrcOps.TelemetryAcceptance;

internal sealed class PostgresRecoveryAcceptance
{
    private readonly string _repository;
    private readonly string _agent;
    private readonly string _inputs;
    private readonly string _scratch;

    public PostgresRecoveryAcceptance(string repository, string agent, string inputs, string scratch)
    {
        _repository = repository;
        _agent = agent;
        _inputs = inputs;
        _scratch = scratch;
    }

    public async Task<object> RunAsync(PostgreSqlContainer original, AcceptanceApi factory, HttpClient api, string readerToken,
        Guid serverId, FaultProxy proxy, string secret)
    {
        var baseline = AcceptanceFiles.ReadInput(_inputs, "ordinary");
        var acknowledged = AcceptanceFiles.ReadInput(_inputs, "lost-ack");
        var pending = AcceptanceFiles.ReadInput(_inputs, "unavailable-restart");
        EntryPoint.Require(baseline.OccurredAtUtc != acknowledged.OccurredAtUtc && baseline.OccurredAtUtc != pending.OccurredAtUtc &&
            acknowledged.OccurredAtUtc != pending.OccurredAtUtc,
            "Reader verification requires three distinct native occurrence times.");
        var directory = Path.Combine(_scratch, "snapshot-recovery");
        await AcceptanceFiles.PrepareAsync(_agent, directory, serverId, proxy, secret, []).ConfigureAwait(false);

        var baselineIdentity = await ImportAsync(directory, serverId, proxy, baseline).ConfigureAwait(false);
        var baselineReceipt = await DeliverAsync(directory, serverId, proxy, baselineIdentity).ConfigureAwait(false);
        await EntryPoint.VerifyPersistenceAsync(factory, api, readerToken, serverId, baselineReceipt, baseline.Event, 1).ConfigureAwait(false);
        var dump = await AcceptancePostgres.DumpAsync(original).ConfigureAwait(false);

        var acknowledgedIdentity = await ImportAsync(directory, serverId, proxy, acknowledged).ConfigureAwait(false);
        var acknowledgedReceipt = await DeliverAsync(directory, serverId, proxy, acknowledgedIdentity).ConfigureAwait(false);
        var pendingIdentity = await ImportAsync(directory, serverId, proxy, pending).ConfigureAwait(false);
        var stateAtCutover = AgentProcess.ReadSnapshot(directory);
        EntryPoint.Require(stateAtCutover is { Entries.Count: 1, SpoolReceipts: 0, NextSequence: 4 } &&
            stateAtCutover.Entries[0].Identity == pendingIdentity && AcceptanceFiles.SpoolFiles(directory).Length == 0 &&
            baselineIdentity.Sequence == 1 && acknowledgedIdentity.Sequence == 2 && pendingIdentity.Sequence == 3 &&
            string.Equals(baselineIdentity.Source, pendingIdentity.Source, StringComparison.Ordinal) &&
            string.Equals(acknowledgedIdentity.Source, pendingIdentity.Source, StringComparison.Ordinal),
            "Expected the same agent source with only the pending event retained at cutover.");
        await EntryPoint.VerifyPersistenceAsync(factory, api, readerToken, serverId, acknowledgedReceipt, acknowledged.Event, 2).ConfigureAwait(false);
        await EntryPoint.VerifyPersistenceAsync(factory, api, readerToken, serverId, baselineReceipt, baseline.Event, 2).ConfigureAwait(false);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await original.StopAsync(deadline.Token).ConfigureAwait(false);
        var recovered = AcceptancePostgres.Build();
        await using var recoveredLifetime = recovered.ConfigureAwait(false);
        await recovered.StartAsync(deadline.Token).ConfigureAwait(false);
        AcceptancePostgres.RequireLocal(recovered);
        EntryPoint.Require(!string.Equals(original.Id, recovered.Id, StringComparison.Ordinal), "Restore must target a fresh container.");
        await AcceptancePostgres.RestoreAsync(recovered, dump, deadline.Token).ConfigureAwait(false);
        var restoredFactory = new AcceptanceApi(_repository, recovered.GetConnectionString());
        await using var restoredFactoryLifetime = restoredFactory.ConfigureAwait(false);
        using var restoredApi = restoredFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = restoredFactory.Address,
        });
        restoredApi.Timeout = TimeSpan.FromSeconds(5);
        var restoredReader = restoredFactory.Token(serverId, reader: true);
        await EntryPoint.VerifyPersistenceAsync(restoredFactory, restoredApi, restoredReader, serverId, baselineReceipt, baseline.Event, 1).ConfigureAwait(false);
        var restoredProxy = await FaultProxy.StartAsync(restoredApi, serverId, restoredFactory.Token(serverId, reader: false), secret).ConfigureAwait(false);
        await using var restoredProxyLifetime = restoredProxy.ConfigureAwait(false);

        EntryPoint.Require(AgentProcess.ReadQueue(directory) == pendingIdentity, "Restore changed the pending agent event before resumption.");
        var pendingReceipt = await DeliverAsync(directory, serverId, restoredProxy, pendingIdentity).ConfigureAwait(false);
        await EntryPoint.VerifyPersistenceAsync(restoredFactory, restoredApi, restoredReader, serverId, baselineReceipt, baseline.Event, 2).ConfigureAwait(false);
        await EntryPoint.VerifyPersistenceAsync(restoredFactory, restoredApi, restoredReader, serverId, pendingReceipt, pending.Event, 2).ConfigureAwait(false);
        EntryPoint.Require(AgentProcess.ReadState(directory) == (pendingIdentity.Source, pendingIdentity.NextSequence) &&
            AcceptanceFiles.SpoolFiles(directory).Length == 0, "Delivery changed the source state or retained a spool file.");

        return new
        {
            status = "native-telemetry-postgres-recovery-accepted",
            snapshot = new { bytes = dump.Length, sha256 = Convert.ToHexString(SHA256.HashData(dump)), freshContainer = true },
            rowsBeforeCutover = 2,
            rowsAfterRestore = 1,
            rowsAfterDelivery = 2,
            events = new[]
            {
                Evidence("in-snapshot", baseline, baselineIdentity, database: true, reader: true, replayed: false),
                Evidence("acknowledged-after-snapshot", acknowledged, acknowledgedIdentity, database: false, reader: false, replayed: false),
                Evidence("pending-at-cutover", pending, pendingIdentity, database: true, reader: true, replayed: true),
            },
            queueDrained = true,
        };
    }

    private async Task<QueueIdentity> ImportAsync(string directory, Guid serverId, FaultProxy proxy, NativeInput input)
    {
        var incoming = Path.Combine(directory, "spool", "incoming", input.RecordId.ToString("D") + ".json");
        await File.WriteAllBytesAsync(incoming, input.Bytes, CancellationToken.None).ConfigureAwait(false);
        var process = AgentProcess.Start(_agent, directory, serverId, proxy.Address, delivery: false, "import-spool");
        await using var lifetime = process.ConfigureAwait(false);
        await process.CompleteAsync().ConfigureAwait(false);
        return AgentProcess.ReadQueue(directory) ?? throw new InvalidOperationException("Native record was not imported.");
    }

    private async Task<GameEventIngestResponse> DeliverAsync(string directory, Guid serverId, FaultProxy proxy, QueueIdentity identity)
    {
        var offset = proxy.Observations.Length;
        var worker = AgentProcess.Start(_agent, directory, serverId, proxy.Address, delivery: true, "run");
        await using (worker.ConfigureAwait(false))
        {
            await EntryPoint.WaitAsync(() => AgentProcess.ReadQueue(directory) is null, worker).ConfigureAwait(false);
        }

        var observations = proxy.Observations[offset..];
        EntryPoint.Require(observations is [ { Status: 202, Receipt.Duplicate: false } ], "Expected exactly one fresh API acceptance.");
        var observation = observations.Single();
        var receipt = observation.Receipt!;
        EntryPoint.Require(receipt.ServerId == serverId && receipt.SequenceNumber == identity.Sequence &&
            string.Equals(receipt.EventId.ToString("D"), identity.EventId, StringComparison.Ordinal) &&
            string.Equals(receipt.SourceInstanceId.ToString("D"), identity.Source, StringComparison.Ordinal) &&
            string.Equals(observation.PayloadSha256, identity.PayloadSha256, StringComparison.Ordinal),
            "API acceptance differs from the queued identity or payload.");
        return receipt;
    }

    private static object Evidence(string position, NativeInput input, QueueIdentity identity, bool database, bool reader, bool replayed) =>
        new { position, nativeSha256 = input.Sha256, identity.EventId, identity.Source, identity.Sequence, database, reader, replayed };
}
