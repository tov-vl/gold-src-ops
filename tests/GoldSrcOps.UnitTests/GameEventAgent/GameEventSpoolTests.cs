using System.Security.Cryptography;
using AwesomeAssertions;
using GoldSrcOps.GameEventAgent;

namespace GoldSrcOps.UnitTests.GameEventAgent;

public sealed class GameEventSpoolTests
{
    [Fact]
    public async Task Writer_and_importer_transfer_one_complete_record_to_the_outbox()
    {
        using var database = new TemporaryAgentDatabase();
        using var spool = new TemporaryAgentSpool();
        var spoolOptions = spool.CreateOptions();
        var time = new TestTimeProvider(GameEventAgentTestData.NowUtc);
        var outbox = new SqliteGameEventOutbox(database.CreateOptions());
        var writer = new GameEventSpoolWriter(spoolOptions, time);
        var importer = new GameEventSpoolImporter(spoolOptions, outbox, time);

        await writer.WriteAsync(GameEventAgentTestData.CreateInput(), CancellationToken.None);
        var before = GameEventSpoolFileSystem.GetStatistics(spoolOptions);
        var result = await importer.ImportBatchAsync(CancellationToken.None);
        var after = GameEventSpoolFileSystem.GetStatistics(spoolOptions);

        before.Ready.Should().Be(1);
        before.Temporary.Should().Be(0);
        result.Imported.Should().Be(1);
        result.Deferred.Should().Be(0);
        result.Rejected.Should().Be(0);
        after.Should().Be(new GameEventSpoolStatistics(0, 0, 0, 0, 0));
        outbox.GetStatistics().Pending.Should().Be(1);
        outbox.GetStatistics().SpoolReceipts.Should().Be(0);
        outbox.GetState().NextSequenceNumber.Should().Be(2);
    }

    [Fact]
    public async Task Importer_reconciles_a_crash_after_the_SQLite_commit_without_a_duplicate()
    {
        using var database = new TemporaryAgentDatabase();
        using var spool = new TemporaryAgentSpool();
        var spoolOptions = spool.CreateOptions();
        var time = new TestTimeProvider(GameEventAgentTestData.NowUtc);
        var outbox = new SqliteGameEventOutbox(database.CreateOptions());
        var writer = new GameEventSpoolWriter(spoolOptions, time);
        var recordId = await writer.WriteAsync(
            GameEventAgentTestData.CreateInput(),
            CancellationToken.None);
        var incomingPath = ReadyPath(spoolOptions, recordId);
        var processingPath = Path.Combine(spoolOptions.ProcessingPath, Path.GetFileName(incomingPath));
        File.Move(incomingPath, processingPath);
        var content = await File.ReadAllBytesAsync(processingPath, CancellationToken.None);
        outbox.EnqueueFromSpool(
            recordId,
            SHA256.HashData(content),
            GameEventAgentTestData.CreateInput(),
            time.GetUtcNow());

        var importer = new GameEventSpoolImporter(spoolOptions, outbox, time);
        var result = await importer.ImportBatchAsync(CancellationToken.None);

        result.Reconciled.Should().Be(1);
        outbox.GetStatistics().Pending.Should().Be(1);
        outbox.GetStatistics().SpoolReceipts.Should().Be(0);
        outbox.GetState().NextSequenceNumber.Should().Be(2);
        GameEventSpoolFileSystem.GetStatistics(spoolOptions)
            .Should().Be(new GameEventSpoolStatistics(0, 0, 0, 0, 0));
    }

    [Fact]
    public async Task Importer_quarantines_an_invalid_record_without_advancing_the_queue()
    {
        using var database = new TemporaryAgentDatabase();
        using var spool = new TemporaryAgentSpool();
        var spoolOptions = spool.CreateOptions();
        GameEventSpoolFileSystem.EnsureDirectories(spoolOptions);
        var recordId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");
        await File.WriteAllTextAsync(
            ReadyPath(spoolOptions, recordId),
            "{",
            CancellationToken.None);
        var outbox = new SqliteGameEventOutbox(database.CreateOptions());
        var importer = new GameEventSpoolImporter(
            spoolOptions,
            outbox,
            new TestTimeProvider(GameEventAgentTestData.NowUtc));

        var result = await importer.ImportBatchAsync(CancellationToken.None);

        result.Rejected.Should().Be(1);
        outbox.GetStatistics().Pending.Should().Be(0);
        outbox.GetState().NextSequenceNumber.Should().Be(1);
        GameEventSpoolFileSystem.GetStatistics(spoolOptions).Rejected.Should().Be(1);
    }

    [Fact]
    public async Task Importer_ignores_temporary_producer_files()
    {
        using var database = new TemporaryAgentDatabase();
        using var spool = new TemporaryAgentSpool();
        var spoolOptions = spool.CreateOptions();
        GameEventSpoolFileSystem.EnsureDirectories(spoolOptions);
        await File.WriteAllTextAsync(
            Path.Combine(spoolOptions.IncomingPath, "producer-write.tmp"),
            "partial",
            CancellationToken.None);
        var outbox = new SqliteGameEventOutbox(database.CreateOptions());
        var importer = new GameEventSpoolImporter(
            spoolOptions,
            outbox,
            new TestTimeProvider(GameEventAgentTestData.NowUtc));

        var result = await importer.ImportBatchAsync(CancellationToken.None);

        result.Processed.Should().Be(0);
        outbox.GetStatistics().Pending.Should().Be(0);
        GameEventSpoolFileSystem.GetStatistics(spoolOptions).Temporary.Should().Be(1);
    }

    [Fact]
    public async Task Importer_leaves_a_claimed_record_for_retry_when_the_queue_is_full()
    {
        using var database = new TemporaryAgentDatabase();
        using var spool = new TemporaryAgentSpool();
        var spoolOptions = spool.CreateOptions();
        var time = new TestTimeProvider(GameEventAgentTestData.NowUtc);
        var outbox = new SqliteGameEventOutbox(database.CreateOptions(capacity: 1));
        outbox.Enqueue(GameEventAgentTestData.CreateInput(), time.GetUtcNow());
        var writer = new GameEventSpoolWriter(spoolOptions, time);
        await writer.WriteAsync(GameEventAgentTestData.CreateInput(), CancellationToken.None);
        var importer = new GameEventSpoolImporter(spoolOptions, outbox, time);

        var result = await importer.ImportBatchAsync(CancellationToken.None);
        var statistics = GameEventSpoolFileSystem.GetStatistics(spoolOptions);

        result.Deferred.Should().Be(1);
        statistics.Ready.Should().Be(0);
        statistics.Processing.Should().Be(1);
        statistics.Rejected.Should().Be(0);
        outbox.GetStatistics().SpoolReceipts.Should().Be(0);
        outbox.GetState().NextSequenceNumber.Should().Be(2);
    }

    [Fact]
    public async Task Importer_finishes_an_accepted_record_left_by_a_crash()
    {
        using var database = new TemporaryAgentDatabase();
        using var spool = new TemporaryAgentSpool();
        var spoolOptions = spool.CreateOptions();
        var time = new TestTimeProvider(GameEventAgentTestData.NowUtc);
        var writer = new GameEventSpoolWriter(spoolOptions, time);
        var recordId = await writer.WriteAsync(
            GameEventAgentTestData.CreateInput(),
            CancellationToken.None);
        var incomingPath = ReadyPath(spoolOptions, recordId);
        var acceptedPath = Path.Combine(spoolOptions.AcceptedPath, Path.GetFileName(incomingPath));
        File.Move(incomingPath, acceptedPath);
        var content = await File.ReadAllBytesAsync(acceptedPath, CancellationToken.None);
        var outbox = new SqliteGameEventOutbox(database.CreateOptions());
        outbox.EnqueueFromSpool(
            recordId,
            SHA256.HashData(content),
            GameEventAgentTestData.CreateInput(),
            time.GetUtcNow());
        var importer = new GameEventSpoolImporter(spoolOptions, outbox, time);

        var result = await importer.ImportBatchAsync(CancellationToken.None);

        result.Finalized.Should().Be(1);
        outbox.GetStatistics().Pending.Should().Be(1);
        outbox.GetStatistics().SpoolReceipts.Should().Be(0);
        GameEventSpoolFileSystem.GetStatistics(spoolOptions).Accepted.Should().Be(0);
    }

    [Fact]
    public async Task Importer_quarantines_record_id_reuse_with_different_bytes()
    {
        using var database = new TemporaryAgentDatabase();
        using var spool = new TemporaryAgentSpool();
        var spoolOptions = spool.CreateOptions();
        var time = new TestTimeProvider(GameEventAgentTestData.NowUtc);
        var writer = new GameEventSpoolWriter(spoolOptions, time);
        var recordId = await writer.WriteAsync(
            GameEventAgentTestData.CreateInput(),
            CancellationToken.None);
        var outbox = new SqliteGameEventOutbox(database.CreateOptions());
        outbox.EnqueueFromSpool(
            recordId,
            SHA256.HashData("different-record"u8),
            GameEventAgentTestData.CreateInput(),
            time.GetUtcNow());
        var importer = new GameEventSpoolImporter(spoolOptions, outbox, time);

        var result = await importer.ImportBatchAsync(CancellationToken.None);

        result.Rejected.Should().Be(1);
        outbox.GetStatistics().Pending.Should().Be(1);
        outbox.GetStatistics().SpoolReceipts.Should().Be(1);
        GameEventSpoolFileSystem.GetStatistics(spoolOptions).Rejected.Should().Be(1);
    }

    [Fact]
    public async Task Importer_processes_no_more_than_the_configured_batch_size()
    {
        using var database = new TemporaryAgentDatabase();
        using var spool = new TemporaryAgentSpool();
        var spoolOptions = spool.CreateOptions(batchSize: 1);
        var time = new TestTimeProvider(GameEventAgentTestData.NowUtc);
        var writer = new GameEventSpoolWriter(spoolOptions, time);
        await writer.WriteAsync(GameEventAgentTestData.CreateInput(), CancellationToken.None);
        await writer.WriteAsync(GameEventAgentTestData.CreateInput(), CancellationToken.None);
        var outbox = new SqliteGameEventOutbox(database.CreateOptions());
        var importer = new GameEventSpoolImporter(spoolOptions, outbox, time);

        var result = await importer.ImportBatchAsync(CancellationToken.None);

        result.Processed.Should().Be(1);
        result.Imported.Should().Be(1);
        outbox.GetStatistics().Pending.Should().Be(1);
        GameEventSpoolFileSystem.GetStatistics(spoolOptions).Ready.Should().Be(1);
    }

    private static string ReadyPath(GameEventSpoolOptions options, Guid recordId) =>
        Path.Combine(options.IncomingPath, GameEventSpoolContract.GetReadyFileName(recordId));
}
