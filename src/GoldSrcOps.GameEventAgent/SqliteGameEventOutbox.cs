using System.Collections.Concurrent;
using System.Globalization;
using GoldSrcOps.Contracts.GameEvents;
using Microsoft.Data.Sqlite;

namespace GoldSrcOps.GameEventAgent;

internal sealed class SqliteGameEventOutbox : IGameEventOutbox
{
    private const int SchemaVersion = 1;
    private const int MaximumFailureCodeLength = 64;

    private static readonly ConcurrentDictionary<string, Lock> InitializationLocks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly GameEventQueueOptions _options;
    private readonly string _connectionString;
    private readonly Lock _initializationLock;
    private bool _initialized;

    public SqliteGameEventOutbox(GameEventQueueOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _initializationLock = InitializationLocks.GetOrAdd(
            Path.GetFullPath(options.DatabasePath),
            static _ => new Lock());
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = options.Pooling,
            DefaultTimeout = checked((int)Math.Ceiling(options.BusyTimeout.TotalSeconds))
        }.ToString();
    }

    public void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        lock (_initializationLock)
        {
            if (_initialized)
            {
                return;
            }

            var directory = Path.GetDirectoryName(_options.DatabasePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var connection = OpenConnection();
            EnsureSchema(connection);
            EnableWriteAheadLogging(connection);
            ValidateState(connection);
            _initialized = true;
        }
    }

    public QueuedGameEvent Enqueue(GameEventSourceInput input, DateTimeOffset enqueuedAtUtc)
    {
        EnsureInitialized();
        var nowUtc = enqueuedAtUtc.ToUniversalTime();
        var normalized = GameEventContractRules.ValidateAndNormalize(input, nowUtc);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);

        using (var countCommand = connection.CreateCommand())
        {
            countCommand.Transaction = transaction;
            countCommand.CommandText = "SELECT COUNT(*) FROM game_event_outbox;";
            var count = Convert.ToInt32(countCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (count >= _options.Capacity)
            {
                throw new GameEventQueueFullException(_options.Capacity);
            }
        }

        var state = ReadState(connection, transaction);
        if (state.NextSequenceNumber == long.MaxValue)
        {
            throw new InvalidOperationException("The game-event source sequence is exhausted.");
        }

        var eventId = Guid.NewGuid();
        var request = new GameEventIngestRequest(
            GameEventContractRules.ContractVersion,
            eventId,
            state.SourceInstanceId,
            state.NextSequenceNumber,
            normalized.Type,
            normalized.OccurredAtUtc,
            normalized.Map,
            normalized.Players,
            normalized.Bots);
        var queued = new QueuedGameEvent(
            eventId,
            state.SourceInstanceId,
            state.NextSequenceNumber,
            GameEventContractRules.ContractVersion,
            normalized.Type,
            normalized.OccurredAtUtc,
            normalized.Map,
            normalized.Players,
            normalized.Bots,
            GameEventJson.SerializeRequest(request),
            nowUtc,
            AttemptCount: 0);

        InsertEvent(connection, transaction, queued);

        using (var sequenceCommand = connection.CreateCommand())
        {
            sequenceCommand.Transaction = transaction;
            sequenceCommand.CommandText = """
                UPDATE agent_state
                SET next_sequence_number = $next_sequence_number
                WHERE singleton_id = 1;
                """;
            sequenceCommand.Parameters.AddWithValue(
                "$next_sequence_number",
                queued.SequenceNumber + 1);
            RequireSingleRow(sequenceCommand.ExecuteNonQuery(), "advance the game-event sequence");
        }

        transaction.Commit();
        return queued;
    }

    public QueuedGameEvent? ClaimNext(DateTimeOffset nowUtc, TimeSpan leaseDuration)
    {
        EnsureInitialized();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);

        var normalizedNow = nowUtc.ToUniversalTime();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        QueuedGameEvent? queued;

        using (var selectCommand = connection.CreateCommand())
        {
            selectCommand.Transaction = transaction;
            selectCommand.CommandText = """
                SELECT
                    event_id,
                    source_instance_id,
                    sequence_number,
                    contract_version,
                    event_type,
                    occurred_at_utc,
                    map,
                    players,
                    bots,
                    payload_json,
                    created_at_utc,
                    attempt_count
                FROM game_event_outbox
                WHERE
                    (status = $pending AND next_attempt_at_utc <= $now_utc)
                    OR (status = $in_flight AND lease_until_utc <= $now_utc)
                ORDER BY sequence_number
                LIMIT 1;
                """;
            selectCommand.Parameters.AddWithValue("$pending", (int)GameEventQueueStatus.Pending);
            selectCommand.Parameters.AddWithValue("$in_flight", (int)GameEventQueueStatus.InFlight);
            selectCommand.Parameters.AddWithValue("$now_utc", FormatTimestamp(normalizedNow));

            using var reader = selectCommand.ExecuteReader();
            queued = reader.Read() ? ReadEvent(reader) : null;
        }

        if (queued is null)
        {
            transaction.Commit();
            return null;
        }

        using (var updateCommand = connection.CreateCommand())
        {
            updateCommand.Transaction = transaction;
            updateCommand.CommandText = """
                UPDATE game_event_outbox
                SET
                    status = $in_flight,
                    attempt_count = attempt_count + 1,
                    lease_until_utc = $lease_until_utc
                WHERE event_id = $event_id;
                """;
            updateCommand.Parameters.AddWithValue("$in_flight", (int)GameEventQueueStatus.InFlight);
            updateCommand.Parameters.AddWithValue(
                "$lease_until_utc",
                FormatTimestamp(normalizedNow + leaseDuration));
            updateCommand.Parameters.AddWithValue("$event_id", queued.EventId.ToString("D"));
            RequireSingleRow(updateCommand.ExecuteNonQuery(), "claim a game event");
        }

        transaction.Commit();
        return queued with { AttemptCount = queued.AttemptCount + 1 };
    }

    public void Acknowledge(Guid eventId)
    {
        EnsureInitialized();
        ExecuteClaimedEventUpdate(
            eventId,
            "DELETE FROM game_event_outbox WHERE event_id = $event_id AND status = $in_flight;",
            configure: null,
            "acknowledge a game event");
    }

    public void Reschedule(
        Guid eventId,
        DateTimeOffset nextAttemptAtUtc,
        GameEventDeliveryFailure failure)
    {
        EnsureInitialized();
        ValidateFailure(failure);
        ExecuteClaimedEventUpdate(
            eventId,
            """
            UPDATE game_event_outbox
            SET
                status = $pending,
                next_attempt_at_utc = $next_attempt_at_utc,
                lease_until_utc = NULL,
                last_failure_code = $failure_code
            WHERE event_id = $event_id AND status = $in_flight;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$pending", (int)GameEventQueueStatus.Pending);
                command.Parameters.AddWithValue(
                    "$next_attempt_at_utc",
                    FormatTimestamp(nextAttemptAtUtc.ToUniversalTime()));
                command.Parameters.AddWithValue("$failure_code", FailureCode(failure));
            },
            "reschedule a game event");
    }

    public void MoveToDeadLetter(Guid eventId, GameEventDeliveryFailure failure)
    {
        EnsureInitialized();
        ValidateFailure(failure);
        ExecuteClaimedEventUpdate(
            eventId,
            """
            UPDATE game_event_outbox
            SET
                status = $dead_letter,
                lease_until_utc = NULL,
                last_failure_code = $failure_code
            WHERE event_id = $event_id AND status = $in_flight;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$dead_letter", (int)GameEventQueueStatus.DeadLetter);
                command.Parameters.AddWithValue("$failure_code", FailureCode(failure));
            },
            "move a game event to dead letter");
    }

    public GameEventQueueState GetState()
    {
        EnsureInitialized();
        using var connection = OpenConnection();
        return ReadState(connection, transaction: null);
    }

    public GameEventQueueStatistics GetStatistics()
    {
        EnsureInitialized();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                SUM(CASE WHEN status = $pending THEN 1 ELSE 0 END),
                SUM(CASE WHEN status = $in_flight THEN 1 ELSE 0 END),
                SUM(CASE WHEN status = $dead_letter THEN 1 ELSE 0 END),
                (SELECT next_sequence_number FROM agent_state WHERE singleton_id = 1)
            FROM game_event_outbox;
            """;
        command.Parameters.AddWithValue("$pending", (int)GameEventQueueStatus.Pending);
        command.Parameters.AddWithValue("$in_flight", (int)GameEventQueueStatus.InFlight);
        command.Parameters.AddWithValue("$dead_letter", (int)GameEventQueueStatus.DeadLetter);

        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(3))
        {
            throw new InvalidOperationException("The game-event queue state is missing.");
        }

        return new GameEventQueueStatistics(
            reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
            reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
            reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
            reader.GetInt64(3));
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        var currentVersion = ReadSchemaVersion(connection, transaction);
        if (currentVersion > SchemaVersion)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant(
                    $"The game-event queue schema version {currentVersion} is newer than supported version {SchemaVersion}."));
        }

        if (currentVersion == SchemaVersion)
        {
            transaction.Commit();
            return;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS agent_state (
                singleton_id INTEGER NOT NULL PRIMARY KEY CHECK (singleton_id = 1),
                source_instance_id TEXT NOT NULL,
                next_sequence_number INTEGER NOT NULL CHECK (next_sequence_number > 0)
            );

            CREATE TABLE IF NOT EXISTS game_event_outbox (
                event_id TEXT NOT NULL PRIMARY KEY,
                source_instance_id TEXT NOT NULL,
                sequence_number INTEGER NOT NULL UNIQUE CHECK (sequence_number > 0),
                contract_version INTEGER NOT NULL CHECK (contract_version > 0),
                event_type TEXT NOT NULL,
                occurred_at_utc TEXT NOT NULL,
                map TEXT NULL,
                players INTEGER NULL,
                bots INTEGER NULL,
                payload_json BLOB NOT NULL CHECK (length(payload_json) <= 4096),
                created_at_utc TEXT NOT NULL,
                status INTEGER NOT NULL,
                attempt_count INTEGER NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
                next_attempt_at_utc TEXT NOT NULL,
                lease_until_utc TEXT NULL,
                last_failure_code TEXT NULL,
                CHECK ((players IS NULL AND bots IS NULL) OR (players IS NOT NULL AND bots IS NOT NULL)),
                CHECK (status IN (0, 1, 2))
            );

            CREATE INDEX IF NOT EXISTS ix_game_event_outbox_due
                ON game_event_outbox(status, next_attempt_at_utc, lease_until_utc, sequence_number);

            PRAGMA user_version = 1;
            """;
        command.ExecuteNonQuery();

        using var stateCommand = connection.CreateCommand();
        stateCommand.Transaction = transaction;
        stateCommand.CommandText = """
            INSERT OR IGNORE INTO agent_state(singleton_id, source_instance_id, next_sequence_number)
            VALUES (1, $source_instance_id, 1);
            """;
        stateCommand.Parameters.AddWithValue("$source_instance_id", Guid.NewGuid().ToString("D"));
        RequireSingleRow(stateCommand.ExecuteNonQuery(), "initialize the game-event source identity");
        transaction.Commit();
    }

    private static void EnableWriteAheadLogging(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = WAL;";
        var result = Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (!string.Equals(result, "wal", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The game-event queue could not enable SQLite WAL mode.");
        }
    }

    private static int ReadSchemaVersion(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void ValidateState(SqliteConnection connection)
    {
        var state = ReadState(connection, transaction: null);
        if (state.SourceInstanceId == Guid.Empty || state.NextSequenceNumber <= 0)
        {
            throw new InvalidOperationException("The game-event queue state is invalid.");
        }
    }

    private static GameEventQueueState ReadState(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT source_instance_id, next_sequence_number
            FROM agent_state
            WHERE singleton_id = 1;
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read() ||
            !Guid.TryParse(reader.GetString(0), out var sourceInstanceId) ||
            sourceInstanceId == Guid.Empty)
        {
            throw new InvalidOperationException("The game-event source identity is missing or invalid.");
        }

        var nextSequenceNumber = reader.GetInt64(1);
        if (nextSequenceNumber <= 0)
        {
            throw new InvalidOperationException("The game-event source sequence is invalid.");
        }

        return new GameEventQueueState(sourceInstanceId, nextSequenceNumber);
    }

    private static void InsertEvent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QueuedGameEvent queued)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO game_event_outbox(
                event_id,
                source_instance_id,
                sequence_number,
                contract_version,
                event_type,
                occurred_at_utc,
                map,
                players,
                bots,
                payload_json,
                created_at_utc,
                status,
                attempt_count,
                next_attempt_at_utc,
                lease_until_utc,
                last_failure_code)
            VALUES (
                $event_id,
                $source_instance_id,
                $sequence_number,
                $contract_version,
                $event_type,
                $occurred_at_utc,
                $map,
                $players,
                $bots,
                $payload_json,
                $created_at_utc,
                $status,
                0,
                $next_attempt_at_utc,
                NULL,
                NULL);
            """;
        command.Parameters.AddWithValue("$event_id", queued.EventId.ToString("D"));
        command.Parameters.AddWithValue("$source_instance_id", queued.SourceInstanceId.ToString("D"));
        command.Parameters.AddWithValue("$sequence_number", queued.SequenceNumber);
        command.Parameters.AddWithValue("$contract_version", queued.ContractVersion);
        command.Parameters.AddWithValue("$event_type", queued.Type);
        command.Parameters.AddWithValue("$occurred_at_utc", FormatTimestamp(queued.OccurredAtUtc));
        command.Parameters.AddWithValue("$map", (object?)queued.Map ?? DBNull.Value);
        command.Parameters.AddWithValue("$players", (object?)queued.Players ?? DBNull.Value);
        command.Parameters.AddWithValue("$bots", (object?)queued.Bots ?? DBNull.Value);
        command.Parameters.Add("$payload_json", SqliteType.Blob).Value = queued.PayloadUtf8;
        command.Parameters.AddWithValue("$created_at_utc", FormatTimestamp(queued.CreatedAtUtc));
        command.Parameters.AddWithValue("$status", (int)GameEventQueueStatus.Pending);
        command.Parameters.AddWithValue("$next_attempt_at_utc", FormatTimestamp(queued.CreatedAtUtc));
        RequireSingleRow(command.ExecuteNonQuery(), "enqueue a game event");
    }

    private static QueuedGameEvent ReadEvent(SqliteDataReader reader) =>
        new(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            reader.GetInt64(2),
            reader.GetInt16(3),
            reader.GetString(4),
            ParseTimestamp(reader.GetString(5)),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetInt32(7),
            reader.IsDBNull(8) ? null : reader.GetInt32(8),
            (byte[])reader.GetValue(9),
            ParseTimestamp(reader.GetString(10)),
            reader.GetInt32(11));

    private void ExecuteClaimedEventUpdate(
        Guid eventId,
        string commandText,
        Action<SqliteCommand>? configure,
        string operation)
    {
        if (eventId == Guid.Empty)
        {
            throw new ArgumentException("Event id must not be empty.", nameof(eventId));
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Parameters.AddWithValue("$event_id", eventId.ToString("D"));
        command.Parameters.AddWithValue("$in_flight", (int)GameEventQueueStatus.InFlight);
        configure?.Invoke(command);
        RequireSingleRow(command.ExecuteNonQuery(), operation);
    }

    private static void ValidateFailure(GameEventDeliveryFailure failure)
    {
        if (failure == GameEventDeliveryFailure.None ||
            FailureCode(failure).Length > MaximumFailureCodeLength)
        {
            throw new ArgumentOutOfRangeException(nameof(failure));
        }
    }

    private static string FailureCode(GameEventDeliveryFailure failure) => failure switch
    {
        GameEventDeliveryFailure.Unauthorized => "unauthorized",
        GameEventDeliveryFailure.Forbidden => "forbidden",
        GameEventDeliveryFailure.ServerNotFound => "server_not_found",
        GameEventDeliveryFailure.Throttled => "throttled",
        GameEventDeliveryFailure.RemoteServer => "remote_server",
        GameEventDeliveryFailure.Network => "network",
        GameEventDeliveryFailure.InvalidReceipt => "invalid_receipt",
        GameEventDeliveryFailure.Conflict => "conflict",
        GameEventDeliveryFailure.InvalidRequest => "invalid_request",
        GameEventDeliveryFailure.Rejected => "rejected",
        GameEventDeliveryFailure.Expired => "expired",
        GameEventDeliveryFailure.MaximumAttempts => "maximum_attempts",
        _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, "Failure code is not supported.")
    };

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            Initialize();
        }
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            .ToUniversalTime();

    private static void RequireSingleRow(int affectedRows, string operation)
    {
        if (affectedRows != 1)
        {
            throw new InvalidOperationException($"Could not {operation}; the queue state changed unexpectedly.");
        }
    }
}
