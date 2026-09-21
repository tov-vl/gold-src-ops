using System.Buffers.Binary;
using Microsoft.AspNetCore.WebUtilities;

namespace GoldSrcOps.AlertReceiver.ProviderOperations;

internal sealed record ProviderDeadLetterPagePosition(
    DateTimeOffset DeadLetteredAtUtc,
    Guid MessageId);

internal static class ProviderDeadLetterCursor
{
    private const byte CurrentVersion = 1;
    private const int CursorByteLength = 25;
    private const int CursorTextLength = 34;

    public static string Encode(ProviderDeadLetterPagePosition position)
    {
        ArgumentNullException.ThrowIfNull(position);
        if (position.MessageId == Guid.Empty)
        {
            throw new ArgumentException("Message id must not be empty.", nameof(position));
        }

        Span<byte> buffer = stackalloc byte[CursorByteLength];
        buffer[0] = CurrentVersion;
        BinaryPrimitives.WriteInt64BigEndian(buffer[1..9], position.DeadLetteredAtUtc.UtcTicks);
        _ = position.MessageId.TryWriteBytes(buffer[9..]);
        return WebEncoders.Base64UrlEncode(buffer);
    }

    public static bool TryDecode(string cursor, out ProviderDeadLetterPagePosition? position)
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
            var deadLetteredAtUtc = new DateTimeOffset(
                BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(1, 8)),
                TimeSpan.Zero);
            var messageId = new Guid(bytes.AsSpan(9, 16));
            if (messageId == Guid.Empty)
            {
                return false;
            }

            position = new ProviderDeadLetterPagePosition(deadLetteredAtUtc, messageId);
            return string.Equals(cursor, Encode(position), StringComparison.Ordinal);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
