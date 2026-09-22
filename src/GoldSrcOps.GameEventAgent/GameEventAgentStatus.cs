namespace GoldSrcOps.GameEventAgent;

internal static class GameEventAgentStatus
{
    public const short SchemaVersion = 1;

    public static GameEventAgentStatusSnapshot Capture(GameEventAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var outbox = new SqliteGameEventOutbox(options.Queue);
        outbox.Initialize();

        return new GameEventAgentStatusSnapshot(
            SchemaVersion,
            options.Spool.Enabled,
            options.Delivery is not null,
            options.Queue.Capacity,
            outbox.GetStatistics(),
            GameEventSpoolFileSystem.GetStatistics(options.Spool));
    }
}

internal sealed record GameEventAgentStatusSnapshot(
    short SchemaVersion,
    bool SpoolImportEnabled,
    bool DeliveryEnabled,
    int QueueCapacity,
    GameEventQueueStatistics Queue,
    GameEventSpoolStatistics Spool);
