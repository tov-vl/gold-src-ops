namespace GoldSrcOps.GameEventAgent;

internal static class GameEventQueueInitialization
{
    public static void CreateFresh(GameEventQueueOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // The installer holds its transition lock and supplies an existing private directory.
        // Refuse recovery debris rather than interpreting it as a new source identity.
        if (!Directory.Exists(Path.GetDirectoryName(options.DatabasePath)) ||
            Path.Exists(options.DatabasePath + "-wal") ||
            Path.Exists(options.DatabasePath + "-shm"))
        {
            throw new InvalidOperationException("Fresh queue initialization requires a prepared empty queue path.");
        }

        using (new FileStream(options.DatabasePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
        }

        // A failed initialization leaves evidence in place; it must not be retried as fresh.
        new SqliteGameEventOutbox(options).Initialize();
    }
}
