using System.Text.Json;

namespace GoldSrcOps.GameEventAgent;

internal sealed partial class GameEventAgentHealth
{
    private const int MaximumBytes = 16 * 1024;
    private readonly Lock _gate = new();
    private readonly GameEventAgentOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GameEventAgentHealth> _logger;
    private GameEventAgentHealthRecord _record;

    public GameEventAgentHealth(
        GameEventAgentOptions options,
        TimeProvider timeProvider,
        ILogger<GameEventAgentHealth> logger)
    {
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _record = new GameEventAgentHealthRecord(
            1, timeProvider.GetUtcNow(), options.Spool.Enabled, options.Delivery is not null,
            Budget(options, import: true), Budget(options, import: false),
            new GameEventAgentCycle(), new GameEventAgentCycle());
    }

    public void RecordStart(bool import) => Update(import, (cycle, nowUtc) => cycle with
    {
        LastStartedAtUtc = nowUtc,
        StoppedAtUtc = null
    });

    public void RecordCompletion(bool import, string outcome) => Update(import, (cycle, nowUtc) => cycle with
    {
        LastCompletedAtUtc = nowUtc,
        LastSuccessfulAtUtc = outcome is "idle" or "progress"
            ? nowUtc : cycle.LastSuccessfulAtUtc,
        Outcome = outcome
    });

    public void RecordStop(bool import) => Update(import, (cycle, nowUtc) => cycle with
    {
        StoppedAtUtc = nowUtc
    });

    private void Update(bool import, Func<GameEventAgentCycle, DateTimeOffset, GameEventAgentCycle> update)
    {
        lock (_gate)
        {
            var nowUtc = _timeProvider.GetUtcNow();
            _record = import
                ? _record with { Import = update(_record.Import, nowUtc) }
                : _record with { Delivery = update(_record.Delivery, nowUtc) };
            var temporary = PathFor(_options) + ".tmp";
            var created = false;
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true;
                    JsonSerializer.Serialize(stream, _record, GameEventJson.SerializerOptions);
                }

                File.Move(temporary, PathFor(_options), overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                LogWriteFailed(_logger);
            }
            finally
            {
                try
                {
                    if (created)
                    {
                        File.Delete(temporary);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    LogWriteFailed(_logger);
                }
            }
        }
    }

    public static string PathFor(GameEventAgentOptions options) => options.Queue.DatabasePath + ".health.json";

    public static GameEventAgentHealthSnapshot Read(GameEventAgentOptions options, DateTimeOffset nowUtc)
    {
        GameEventAgentHealthRecord? record = null;
        try
        {
            using var stream = new FileStream(PathFor(options), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[MaximumBytes + 1];
            var count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            if (count <= MaximumBytes)
            {
                record = JsonSerializer.Deserialize<GameEventAgentHealthRecord>(
                    bytes.AsSpan(0, count), GameEventJson.StrictSerializerOptions);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Missing or unreadable diagnostic evidence must never imply a live worker.
        }

        if (record is not { SchemaVersion: 1, Import: not null, Delivery: not null } ||
            record.StartedAtUtc > nowUtc || record.ImportEnabled != options.Spool.Enabled ||
            record.DeliveryEnabled != options.Delivery is not null ||
            record.ImportStaleAfterSeconds != Budget(options, import: true) ||
            record.DeliveryStaleAfterSeconds != Budget(options, import: false) ||
            !ValidCycle(record.Import) || !ValidCycle(record.Delivery))
        {
            record = null;
        }

        return new GameEventAgentHealthSnapshot(
            record?.StartedAtUtc,
            Assess(record?.Import, options.Spool.Enabled, Budget(options, import: true), nowUtc),
            Assess(record?.Delivery, options.Delivery is not null, Budget(options, import: false), nowUtc));
    }

    private static GameEventAgentCycleStatus Assess(
        GameEventAgentCycle? cycle, bool enabled, long budget, DateTimeOffset nowUtc)
    {
        if (!enabled)
        {
            return new GameEventAgentCycleStatus("disabled", budget, cycle);
        }

        if (cycle?.LastStartedAtUtc is null ||
            cycle.LastStartedAtUtc > nowUtc || cycle.LastCompletedAtUtc > nowUtc ||
            cycle.LastSuccessfulAtUtc > nowUtc || cycle.StoppedAtUtc > nowUtc)
        {
            return new GameEventAgentCycleStatus("unavailable", budget, cycle);
        }

        if (cycle.StoppedAtUtc is not null)
        {
            return new GameEventAgentCycleStatus("stopped", budget, cycle);
        }

        var latest = cycle.LastCompletedAtUtc > cycle.LastStartedAtUtc
            ? cycle.LastCompletedAtUtc.Value : cycle.LastStartedAtUtc.Value;
        var state = (nowUtc - latest).TotalSeconds > budget ? "stale"
            : cycle.LastCompletedAtUtc is null ? "starting"
            : cycle.LastStartedAtUtc > cycle.LastCompletedAtUtc ? "running"
            : cycle.Outcome is "failed" or "retrying" or "blocked" ? cycle.Outcome
            : "running";
        return new GameEventAgentCycleStatus(state, budget, cycle);
    }

    private static bool ValidCycle(GameEventAgentCycle cycle) =>
        cycle.Outcome is null or "idle" or "progress" or "failed" or "retrying" or "blocked" &&
        (cycle.LastCompletedAtUtc is null ? cycle.Outcome is null : cycle.Outcome is not null) &&
        (cycle.LastCompletedAtUtc is null || cycle.LastStartedAtUtc is not null) &&
        (cycle.LastSuccessfulAtUtc is null || cycle.LastCompletedAtUtc >= cycle.LastSuccessfulAtUtc);

    private static long Budget(GameEventAgentOptions options, bool import)
    {
        // Include bounded sequential work, not just the delay between passes.
        var seconds = import
            ? 10 + 2 * options.Spool.ImportInterval.TotalSeconds +
                3 * options.Spool.BatchSize * options.Queue.BusyTimeout.TotalSeconds
            : options.Delivery is { } delivery
                ? 10 + 2 * delivery.DispatchInterval.TotalSeconds +
                    delivery.BatchSize * (2 * delivery.RequestTimeout.TotalSeconds +
                        3 * options.Queue.BusyTimeout.TotalSeconds)
                : 0;
        return checked((long)Math.Ceiling(seconds));
    }

    [LoggerMessage(EventId = 2201, Level = LogLevel.Warning,
        Message = "Game-event diagnostic record could not be written; worker processing is unchanged.")]
    private static partial void LogWriteFailed(ILogger logger);
}

internal sealed record GameEventAgentHealthRecord(
    short SchemaVersion,
    DateTimeOffset StartedAtUtc,
    bool ImportEnabled,
    bool DeliveryEnabled,
    long ImportStaleAfterSeconds,
    long DeliveryStaleAfterSeconds,
    GameEventAgentCycle Import,
    GameEventAgentCycle Delivery);

internal sealed record GameEventAgentCycle(
    DateTimeOffset? LastStartedAtUtc = null,
    DateTimeOffset? LastCompletedAtUtc = null,
    DateTimeOffset? LastSuccessfulAtUtc = null,
    DateTimeOffset? StoppedAtUtc = null,
    string? Outcome = null);

internal sealed record GameEventAgentCycleStatus(
    string State, long StaleAfterSeconds, GameEventAgentCycle? Cycle);

internal sealed record GameEventAgentHealthSnapshot(
    DateTimeOffset? StartedAtUtc, GameEventAgentCycleStatus Import, GameEventAgentCycleStatus Delivery);
