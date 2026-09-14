using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GoldSrcOps.GameEventAgent;

internal sealed class GameEventSpoolImporter(
    GameEventSpoolOptions options,
    IGameEventOutbox outbox,
    TimeProvider timeProvider)
{
    public async Task<GameEventSpoolImportBatchResult> ImportBatchAsync(
        CancellationToken cancellationToken)
    {
        GameEventSpoolFileSystem.EnsureDirectories(options);
        outbox.Initialize();

        var result = GameEventSpoolImportBatchResult.Empty;
        var remaining = options.BatchSize;

        foreach (var path in Enumerate(options.AcceptedPath, remaining))
        {
            result = result.Add(await FinalizeAcceptedAsync(path, cancellationToken).ConfigureAwait(false));
            remaining--;
        }

        foreach (var path in Enumerate(options.ProcessingPath, remaining))
        {
            result = result.Add(await ImportProcessingAsync(path, cancellationToken).ConfigureAwait(false));
            remaining--;
        }

        foreach (var incomingPath in Enumerate(options.IncomingPath, remaining))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var processingPath = Path.Combine(
                options.ProcessingPath,
                Path.GetFileName(incomingPath));
            try
            {
                File.Move(incomingPath, processingPath, overwrite: false);
            }
            catch (Exception exception) when (IsDeferredFileSystemFailure(exception))
            {
                result = result.Add(GameEventSpoolImportDisposition.Deferred);
                remaining--;
                continue;
            }

            result = result.Add(
                await ImportProcessingAsync(processingPath, cancellationToken).ConfigureAwait(false));
            remaining--;
        }

        return result;
    }

    private async Task<GameEventSpoolImportDisposition> ImportProcessingAsync(
        string processingPath,
        CancellationToken cancellationToken)
    {
        ParsedGameEventSpoolRecord record;
        try
        {
            record = await ReadRecordAsync(processingPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsInvalidRecord(exception))
        {
            return MoveToRejected(processingPath, "invalid-record");
        }
        catch (Exception exception) when (IsDeferredFileSystemFailure(exception))
        {
            return GameEventSpoolImportDisposition.Deferred;
        }

        GameEventSpoolEnqueueResult enqueueResult;
        try
        {
            enqueueResult = outbox.EnqueueFromSpool(
                record.RecordId,
                record.ContentSha256,
                record.Event,
                timeProvider.GetUtcNow());
        }
        catch (GameEventSpoolRecordConflictException)
        {
            return MoveToRejected(processingPath, "record-conflict");
        }
        catch (Exception exception) when (IsDeferredQueueFailure(exception))
        {
            return GameEventSpoolImportDisposition.Deferred;
        }

        var acceptedPath = Path.Combine(
            options.AcceptedPath,
            Path.GetFileName(processingPath));
        try
        {
            File.Move(processingPath, acceptedPath, overwrite: false);
        }
        catch (Exception exception) when (IsDeferredFileSystemFailure(exception))
        {
            return GameEventSpoolImportDisposition.Deferred;
        }

        var finalized = await FinalizeAcceptedAsync(acceptedPath, cancellationToken).ConfigureAwait(false);
        if (finalized != GameEventSpoolImportDisposition.Finalized)
        {
            return finalized;
        }

        return enqueueResult.AlreadyQueued
            ? GameEventSpoolImportDisposition.Reconciled
            : GameEventSpoolImportDisposition.Imported;
    }

    private async Task<GameEventSpoolImportDisposition> FinalizeAcceptedAsync(
        string acceptedPath,
        CancellationToken cancellationToken)
    {
        ParsedGameEventSpoolRecord record;
        try
        {
            record = await ReadRecordAsync(acceptedPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsInvalidRecord(exception))
        {
            return MoveToRejected(acceptedPath, "invalid-accepted-record");
        }
        catch (Exception exception) when (IsDeferredFileSystemFailure(exception))
        {
            return GameEventSpoolImportDisposition.Deferred;
        }

        try
        {
            outbox.CompleteSpoolRecord(record.RecordId, record.ContentSha256);
            File.Delete(acceptedPath);
            return GameEventSpoolImportDisposition.Finalized;
        }
        catch (GameEventSpoolRecordConflictException)
        {
            return MoveToRejected(acceptedPath, "accepted-record-conflict");
        }
        catch (Exception exception) when (
            IsDeferredQueueFailure(exception) || IsDeferredFileSystemFailure(exception))
        {
            return GameEventSpoolImportDisposition.Deferred;
        }
    }

    private async Task<ParsedGameEventSpoolRecord> ReadRecordAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (GameEventSpoolFileSystem.IsReparsePoint(path))
        {
            throw new InvalidDataException("A game-event spool record must not be a reparse point.");
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None,
            bufferSize: 4 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > GameEventSpoolContract.MaximumRecordBytes)
        {
            throw new InvalidDataException("The game-event spool record is empty or exceeds 4 KiB.");
        }

        var content = new byte[checked((int)stream.Length)];
        var offset = 0;
        while (offset < content.Length)
        {
            var read = await stream.ReadAsync(
                content.AsMemory(offset),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The game-event spool record ended unexpectedly.");
            }

            offset += read;
        }

        return GameEventSpoolContract.Parse(
            content,
            Path.GetFileName(path),
            timeProvider.GetUtcNow());
    }

    private GameEventSpoolImportDisposition MoveToRejected(string sourcePath, string reason)
    {
        var rejectedName = FormattableString.Invariant(
            $"{timeProvider.GetUtcNow().ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}-{reason}.json");
        try
        {
            File.Move(
                sourcePath,
                Path.Combine(options.RejectedPath, rejectedName),
                overwrite: false);
            return GameEventSpoolImportDisposition.Rejected;
        }
        catch (Exception exception) when (IsDeferredFileSystemFailure(exception))
        {
            return GameEventSpoolImportDisposition.Deferred;
        }
    }

    private static string[] Enumerate(string path, int maximumCount)
    {
        if (maximumCount <= 0)
        {
            return [];
        }

        return Directory
            .EnumerateFiles(path, "*.json", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .Take(maximumCount)
            .ToArray();
    }

    private static bool IsInvalidRecord(Exception exception) =>
        exception is InvalidDataException or JsonException or ArgumentException;

    private static bool IsDeferredFileSystemFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException;

    private static bool IsDeferredQueueFailure(Exception exception) =>
        exception is GameEventQueueFullException or
            GameEventSpoolReceiptCapacityException or
            SqliteException;
}

internal enum GameEventSpoolImportDisposition
{
    Imported,
    Reconciled,
    Finalized,
    Rejected,
    Deferred
}

internal sealed record GameEventSpoolImportBatchResult(
    int Imported,
    int Reconciled,
    int Finalized,
    int Rejected,
    int Deferred)
{
    public static GameEventSpoolImportBatchResult Empty { get; } = new(0, 0, 0, 0, 0);

    public int Processed => Imported + Reconciled + Finalized + Rejected + Deferred;

    public GameEventSpoolImportBatchResult Add(GameEventSpoolImportDisposition disposition) =>
        disposition switch
        {
            GameEventSpoolImportDisposition.Imported => this with { Imported = Imported + 1 },
            GameEventSpoolImportDisposition.Reconciled => this with { Reconciled = Reconciled + 1 },
            GameEventSpoolImportDisposition.Finalized => this with { Finalized = Finalized + 1 },
            GameEventSpoolImportDisposition.Rejected => this with { Rejected = Rejected + 1 },
            GameEventSpoolImportDisposition.Deferred => this with { Deferred = Deferred + 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Disposition is not supported.")
        };
}
