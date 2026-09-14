using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using GoldSrcOps.Application.Common;
using GoldSrcOps.Application.Telemetry;
using GoldSrcOps.Domain.GameEvents;

namespace GoldSrcOps.Application.GameEvents;

public sealed class GameEventIngestionService
{
    public static readonly TimeSpan MaximumFutureClockSkew = TimeSpan.FromMinutes(5);

    private readonly IGameEventInboxRepository _inbox;
    private readonly IClock _clock;

    public GameEventIngestionService(IGameEventInboxRepository inbox, IClock clock)
    {
        _inbox = inbox;
        _clock = clock;
    }

    public async Task<GameEventIngestResult> IngestAsync(
        Guid serverId,
        IngestGameEventCommand command,
        CancellationToken cancellationToken)
    {
        var receivedAtUtc = TruncateToMicroseconds(_clock.UtcNow.ToUniversalTime());
        var occurredAtUtc = TruncateToMicroseconds(command.OccurredAtUtc.ToUniversalTime());
        if (occurredAtUtc > receivedAtUtc + MaximumFutureClockSkew)
        {
            GoldSrcOpsMetrics.RecordGameEventIngestion(
                command.Type,
                GameEventIngestionMetricResult.Rejected);
            return GameEventIngestResult.OccurredAtInFuture();
        }

        if (!await _inbox.ServerExistsAsync(serverId, cancellationToken))
        {
            GoldSrcOpsMetrics.RecordGameEventIngestion(
                command.Type,
                GameEventIngestionMetricResult.ServerNotFound);
            return GameEventIngestResult.ServerNotFound();
        }

        var normalizedMap = string.IsNullOrWhiteSpace(command.Map)
            ? null
            : command.Map.Trim();
        var intentHash = CreateIntentHash(serverId, command, occurredAtUtc, normalizedMap);
        var entry = new GameEventInboxEntry(
            command.EventId,
            serverId,
            command.SourceInstanceId,
            command.SequenceNumber,
            command.ContractVersion,
            command.Type,
            occurredAtUtc,
            receivedAtUtc,
            normalizedMap,
            command.Players,
            command.Bots,
            intentHash);

        var persistence = await _inbox.StoreAsync(entry, cancellationToken);
        switch (persistence.Kind)
        {
            case GameEventInboxPersistenceResultKind.Created:
                GoldSrcOpsMetrics.RecordGameEventIngestion(
                    command.Type,
                    GameEventIngestionMetricResult.Accepted);
                return GameEventIngestResult.Accepted(Map(persistence.Entry, duplicate: false));
            case GameEventInboxPersistenceResultKind.EventIdExists:
                if (string.Equals(
                    persistence.Entry.IntentHash,
                    intentHash,
                    StringComparison.Ordinal))
                {
                    GoldSrcOpsMetrics.RecordGameEventIngestion(
                        command.Type,
                        GameEventIngestionMetricResult.Idempotent);
                    return GameEventIngestResult.Idempotent(Map(persistence.Entry, duplicate: true));
                }

                GoldSrcOpsMetrics.RecordGameEventIngestion(
                    command.Type,
                    GameEventIngestionMetricResult.EventIdConflict);
                return GameEventIngestResult.EventIdConflict();
            case GameEventInboxPersistenceResultKind.SourceSequenceExists:
                GoldSrcOpsMetrics.RecordGameEventIngestion(
                    command.Type,
                    GameEventIngestionMetricResult.SourceSequenceConflict);
                return GameEventIngestResult.SourceSequenceConflict();
            default:
                throw new InvalidOperationException(
                    $"Unsupported game event persistence result '{persistence.Kind}'.");
        }
    }

    private static GameEventIngestDto Map(GameEventInboxEntry entry, bool duplicate) =>
        new(
            entry.Id,
            entry.ServerId,
            entry.SourceInstanceId,
            entry.SequenceNumber,
            entry.ContractVersion,
            entry.Type,
            entry.OccurredAtUtc,
            entry.ReceivedAtUtc,
            duplicate);

    private static string CreateIntentHash(
        Guid serverId,
        IngestGameEventCommand command,
        DateTimeOffset occurredAtUtc,
        string? normalizedMap)
    {
        var payload = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(payload))
        {
            writer.WriteStartObject();
            writer.WriteString("serverId", serverId);
            writer.WriteString("eventId", command.EventId);
            writer.WriteString("sourceInstanceId", command.SourceInstanceId);
            writer.WriteNumber("sequenceNumber", command.SequenceNumber);
            writer.WriteNumber("contractVersion", command.ContractVersion);
            writer.WriteString("type", GameEventTypeContract.ToWireValue(command.Type));
            writer.WriteString("occurredAtUtc", occurredAtUtc);
            WriteNullableString(writer, "map", normalizedMap);
            WriteNullableNumber(writer, "players", command.Players);
            WriteNullableNumber(writer, "bots", command.Bots);
            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(payload.WrittenSpan));
    }

    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value) =>
        value.AddTicks(-(value.Ticks % TimeSpan.TicksPerMicrosecond));

    private static void WriteNullableString(
        Utf8JsonWriter writer,
        string propertyName,
        string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, value);
        }
    }

    private static void WriteNullableNumber(
        Utf8JsonWriter writer,
        string propertyName,
        int? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteNumber(propertyName, value.Value);
        }
    }
}
