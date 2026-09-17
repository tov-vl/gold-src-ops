using System.Buffers.Binary;
using GoldSrcOps.Application.Monitoring;
using Microsoft.AspNetCore.WebUtilities;

namespace GoldSrcOps.Api.Endpoints;

internal sealed record OperationsActivityCursorScope(
    int Limit,
    Guid? ServerId,
    OperationsActivitySource? Source,
    OperationsActivityWindow? Window);

internal static class OperationsActivityCursor
{
    private const byte CurrentVersion = 1;
    private const byte HasServerId = 1;
    private const int CursorByteLength = 34;
    private const int CursorTextLength = 46;

    public static string Encode(
        OperationsActivityPagePosition position,
        OperationsActivityCursorScope scope)
    {
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(scope);

        if (position.Offset is < 0 or > MonitoringReadService.MaxActivityOffset)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        if (scope.Limit is < 1 or > MonitoringReadService.MaxActivityLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(scope));
        }

        Span<byte> buffer = stackalloc byte[CursorByteLength];
        buffer.Clear();
        buffer[0] = CurrentVersion;
        buffer[1] = scope.ServerId is null ? (byte)0 : HasServerId;
        BinaryPrimitives.WriteInt32BigEndian(buffer[2..6], position.Offset);
        BinaryPrimitives.WriteUInt16BigEndian(buffer[6..8], checked((ushort)scope.Limit));
        if (scope.ServerId is not null)
        {
            _ = scope.ServerId.Value.TryWriteBytes(buffer[8..24]);
        }

        buffer[24] = EncodeSource(scope.Source);
        buffer[25] = EncodeWindow(scope.Window);
        BinaryPrimitives.WriteInt64BigEndian(buffer[26..34], position.ToUtc.UtcTicks);

        return WebEncoders.Base64UrlEncode(buffer);
    }

    public static bool TryDecode(
        string cursor,
        OperationsActivityCursorScope expectedScope,
        out OperationsActivityPagePosition? position)
    {
        ArgumentNullException.ThrowIfNull(expectedScope);
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
            bytes[1] > HasServerId)
        {
            return false;
        }

        var offset = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(2, 4));
        var limit = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(6, 2));
        var serverId = new Guid(bytes.AsSpan(8, 16));
        var hasServerId = bytes[1] == HasServerId;
        if (offset is < 0 or > MonitoringReadService.MaxActivityOffset ||
            limit is < 1 or > MonitoringReadService.MaxActivityLimit ||
            (!hasServerId && serverId != Guid.Empty) ||
            !TryDecodeSource(bytes[24], out var source) ||
            !TryDecodeWindow(bytes[25], out var window))
        {
            return false;
        }

        var scope = new OperationsActivityCursorScope(
            limit,
            hasServerId ? serverId : null,
            source,
            window);
        if (scope != expectedScope)
        {
            return false;
        }

        try
        {
            var toUtc = new DateTimeOffset(
                BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(26, 8)),
                TimeSpan.Zero);
            if (toUtc.Ticks % TimeSpan.TicksPerMicrosecond != 0)
            {
                return false;
            }

            position = new OperationsActivityPagePosition(offset, toUtc);
            return string.Equals(cursor, Encode(position, scope), StringComparison.Ordinal);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static byte EncodeSource(OperationsActivitySource? source) => source switch
    {
        null => 0,
        OperationsActivitySource.Incident => 1,
        OperationsActivitySource.Command => 2,
        OperationsActivitySource.Gameplay => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(source))
    };

    private static bool TryDecodeSource(byte value, out OperationsActivitySource? source)
    {
        source = value switch
        {
            0 => null,
            1 => OperationsActivitySource.Incident,
            2 => OperationsActivitySource.Command,
            3 => OperationsActivitySource.Gameplay,
            _ => null
        };

        return value <= 3;
    }

    private static byte EncodeWindow(OperationsActivityWindow? window) => window switch
    {
        null => 0,
        OperationsActivityWindow.LastHour => 1,
        OperationsActivityWindow.Last6Hours => 2,
        OperationsActivityWindow.Last24Hours => 3,
        OperationsActivityWindow.Last7Days => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(window))
    };

    private static bool TryDecodeWindow(byte value, out OperationsActivityWindow? window)
    {
        window = value switch
        {
            0 => null,
            1 => OperationsActivityWindow.LastHour,
            2 => OperationsActivityWindow.Last6Hours,
            3 => OperationsActivityWindow.Last24Hours,
            4 => OperationsActivityWindow.Last7Days,
            _ => null
        };

        return value <= 4;
    }
}
