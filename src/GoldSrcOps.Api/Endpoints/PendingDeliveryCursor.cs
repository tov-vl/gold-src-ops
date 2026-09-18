using System.Buffers.Binary;
using GoldSrcOps.Application.Alerts;
using Microsoft.AspNetCore.WebUtilities;

namespace GoldSrcOps.Api.Endpoints;

internal static class PendingDeliveryCursor
{
    private const byte CurrentVersion = 1;
    private const int CursorByteLength = 33;
    private const int CursorTextLength = 44;

    public static string Encode(PendingDeliveryPagePosition position)
    {
        ArgumentNullException.ThrowIfNull(position);
        if (position.EventId == Guid.Empty)
        {
            throw new ArgumentException("Event id must not be empty.", nameof(position));
        }

        Span<byte> buffer = stackalloc byte[CursorByteLength];
        buffer[0] = CurrentVersion;
        BinaryPrimitives.WriteInt64BigEndian(buffer[1..9], position.NextAttemptAtUtc.UtcTicks);
        BinaryPrimitives.WriteInt64BigEndian(buffer[9..17], position.OccurredAtUtc.UtcTicks);
        _ = position.EventId.TryWriteBytes(buffer[17..]);

        return WebEncoders.Base64UrlEncode(buffer);
    }

    public static bool TryDecode(string cursor, out PendingDeliveryPagePosition? position)
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

        if (bytes.Length != CursorByteLength || bytes[0] != CurrentVersion)
        {
            return false;
        }

        try
        {
            var nextAttemptAtUtc = new DateTimeOffset(
                BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(1, 8)),
                TimeSpan.Zero);
            var occurredAtUtc = new DateTimeOffset(
                BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(9, 8)),
                TimeSpan.Zero);
            var eventId = new Guid(bytes.AsSpan(17, 16));
            if (eventId == Guid.Empty)
            {
                return false;
            }

            position = new PendingDeliveryPagePosition(nextAttemptAtUtc, occurredAtUtc, eventId);
            return string.Equals(cursor, Encode(position), StringComparison.Ordinal);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
