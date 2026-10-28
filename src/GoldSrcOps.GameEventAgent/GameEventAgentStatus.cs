namespace GoldSrcOps.GameEventAgent;

internal static class GameEventAgentStatus
{
    public const short SchemaVersion = 1;

    public static GameEventAgentStatusSnapshot Capture(GameEventAgentOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var nowUtc = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var queue = GameEventQueueStatusReader.Read(options.Queue, nowUtc);

        return new GameEventAgentStatusSnapshot(
            SchemaVersion,
            options.Spool.Enabled,
            options.Delivery is not null,
            options.Queue.Capacity,
            queue.Statistics,
            GameEventSpoolFileSystem.GetStatistics(options.Spool),
            nowUtc,
            queue.OldestWaitingAtUtc,
            queue.OldestWaitingAgeSeconds,
            Directory.Exists(options.Spool.IncomingPath) && Directory.Exists(options.Spool.ProcessingPath) &&
                Directory.Exists(options.Spool.AcceptedPath) && Directory.Exists(options.Spool.RejectedPath),
            GameEventAgentHealth.Read(options, nowUtc));
    }
}

internal sealed record GameEventAgentStatusSnapshot(
    short SchemaVersion,
    bool SpoolImportEnabled,
    bool DeliveryEnabled,
    int QueueCapacity,
    GameEventQueueStatistics Queue,
    GameEventSpoolStatistics Spool,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset? OldestWaitingAtUtc,
    double? OldestWaitingAgeSeconds,
    bool SpoolAvailable,
    GameEventAgentHealthSnapshot Health);
