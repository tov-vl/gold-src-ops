using System.Buffers.Binary;
using GoldSrcOps.Application.Alerts;
using Microsoft.AspNetCore.WebUtilities;

namespace GoldSrcOps.Api.Endpoints;

internal static class DeadLetterCursor
{
    private const byte CurrentVersion = 1;
    private const byte HasDeadLetterTimestamp = 1;
    private const int CursorByteLength = 34;
    private const int CursorTextLength = 46;

    public static string Encode(DeadLetterPagePosition position)
    {
        ArgumentNullException.ThrowIfNull(position);
        if (position.EventId == Guid.Empty)
        {
            throw new ArgumentException("Event id must not be empty.", nameof(position));
        }

        Span<byte> buffer = stackalloc byte[CursorByteLength];
        buffer[0] = CurrentVersion;
        buffer[1] = position.DeadLetteredAtUtc is null ? (byte)0 : HasDeadLetterTimestamp;
        BinaryPrimitives.WriteInt64BigEndian(
            buffer[2..10],
            position.DeadLetteredAtUtc?.UtcTicks ?? 0);
        BinaryPrimitives.WriteInt64BigEndian(buffer[10..18], position.OccurredAtUtc.UtcTicks);
        _ = position.EventId.TryWriteBytes(buffer[18..]);

        return WebEncoders.Base64UrlEncode(buffer);
    }

    public static bool TryDecode(string cursor, out DeadLetterPagePosition? position)
    {
        position = null;
        if (cursor.Length != CursorTextLength)
        {
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = WebEncoders.Base64UrlDecode(cursor);
        }
        catch (FormatException)
        {
            return false;
        }

        if (bytes.Length != CursorByteLength ||
            bytes[0] != CurrentVersion ||
            bytes[1] > HasDeadLetterTimestamp)
        {
            return false;
        }

        var deadLetterTicks = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(2, 8));
        if (bytes[1] == 0 && deadLetterTicks != 0)
        {
            return false;
        }

        try
        {
            var deadLetteredAtUtc = bytes[1] == HasDeadLetterTimestamp
                ? new DateTimeOffset(deadLetterTicks, TimeSpan.Zero)
                : (DateTimeOffset?)null;
            var occurredAtUtc = new DateTimeOffset(
                BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(10, 8)),
                TimeSpan.Zero);
            var eventId = new Guid(bytes.AsSpan(18, 16));
            if (eventId == Guid.Empty)
            {
                return false;
            }

            position = new DeadLetterPagePosition(deadLetteredAtUtc, occurredAtUtc, eventId);
            return string.Equals(cursor, Encode(position), StringComparison.Ordinal);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
