using System.Globalization;
using Microsoft.Data.Sqlite;

namespace GoldSrcOps.GameEventAgent;

internal static class GameEventQueueStatusReader
{
    public static GameEventQueueObservation Read(GameEventQueueOptions options, DateTimeOffset nowUtc)
    {
        if (!File.Exists(options.DatabasePath))
        {
            throw new InvalidOperationException("Game-event queue is missing; status does not initialize it.");
        }

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = checked((int)Math.Ceiling(options.BusyTimeout.TotalSeconds))
        }.ToString());
        try
        {
            connection.Open();
            using var version = connection.CreateCommand();
            version.CommandText = "PRAGMA user_version;";
            if (Convert.ToInt32(version.ExecuteScalar(), CultureInfo.InvariantCulture) != 2)
            {
                throw new InvalidOperationException("Game-event queue schema is unsupported; status does not migrate it.");
            }

            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    COALESCE(SUM(CASE WHEN status = 0 THEN 1 ELSE 0 END), 0),
                    COALESCE(SUM(CASE WHEN status = 1 THEN 1 ELSE 0 END), 0),
                    COALESCE(SUM(CASE WHEN status = 2 THEN 1 ELSE 0 END), 0),
                    (SELECT COUNT(*) FROM game_event_spool_receipt),
                    (SELECT next_sequence_number FROM agent_state WHERE singleton_id = 1),
                    MIN(CASE WHEN status IN (0, 1) THEN created_at_utc END)
                FROM game_event_outbox;
                """;
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.IsDBNull(4))
            {
                throw new InvalidOperationException("Game-event queue state is unavailable.");
            }

            var oldest = reader.IsDBNull(5) ? (DateTimeOffset?)null
                : DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture);
            return new GameEventQueueObservation(
                new GameEventQueueStatistics(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2),
                    reader.GetInt32(3), reader.GetInt64(4)),
                oldest,
                oldest is null ? null : Math.Max(0, (nowUtc - oldest.Value).TotalSeconds));
        }
        catch (Exception exception) when (exception is SqliteException or FormatException)
        {
            throw new InvalidOperationException("Game-event queue is unreadable or busy; no empty-queue claim is available.");
        }
    }
}

internal sealed record GameEventQueueObservation(
    GameEventQueueStatistics Statistics, DateTimeOffset? OldestWaitingAtUtc, double? OldestWaitingAgeSeconds);
