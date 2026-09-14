using System.Security.Cryptography;
using System.Text.Json;

namespace GoldSrcOps.GameEventAgent;

internal static class GameEventSpoolContract
{
    public const short Version = 1;
    public const int MaximumRecordBytes = 4 * 1024;

    public static string GetReadyFileName(Guid recordId)
    {
        if (recordId == Guid.Empty)
        {
            throw new ArgumentException("Spool record id must not be empty.", nameof(recordId));
        }

        return string.Concat(recordId.ToString("D"), ".json");
    }

    public static ParsedGameEventSpoolRecord Parse(
        byte[] contentUtf8,
        string fileName,
        DateTimeOffset receivedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(contentUtf8);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        if (contentUtf8.Length is <= 0 or > MaximumRecordBytes)
        {
            throw new InvalidDataException("The game-event spool record is empty or exceeds 4 KiB.");
        }

        var envelope = JsonSerializer.Deserialize<GameEventSpoolEnvelope>(
            contentUtf8,
            GameEventJson.StrictSerializerOptions) ??
            throw new InvalidDataException("The game-event spool record contains no envelope.");
        if (envelope.SpoolVersion != Version)
        {
            throw new InvalidDataException("The game-event spool version is not supported.");
        }

        if (envelope.RecordId == Guid.Empty || envelope.Event is null)
        {
            throw new InvalidDataException("The game-event spool identity or event is missing.");
        }

        if (!string.Equals(
                fileName,
                GetReadyFileName(envelope.RecordId),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The game-event spool file name is not canonical.");
        }

        var normalized = GameEventContractRules.ValidateAndNormalize(
            envelope.Event,
            receivedAtUtc.ToUniversalTime());
        return new ParsedGameEventSpoolRecord(
            envelope.RecordId,
            SHA256.HashData(contentUtf8),
            normalized);
    }
}

internal sealed record ParsedGameEventSpoolRecord(
    Guid RecordId,
    byte[] ContentSha256,
    GameEventSourceInput Event);
