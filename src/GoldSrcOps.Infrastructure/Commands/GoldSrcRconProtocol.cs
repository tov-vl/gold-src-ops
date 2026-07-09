using System.Buffers.Binary;
using System.Text;

namespace GoldSrcOps.Infrastructure.Commands;

internal static class GoldSrcRconProtocol
{
    private const int HeaderLength = sizeof(int);
    private const string ChallengeRequestText = "challenge rcon\n";
    private const string ChallengeResponsePrefix = "challenge rcon ";
    private const string BadPasswordText = "Bad rcon_password";

    public static byte[] BuildChallengeRequest(Encoding encoding) =>
        BuildDatagram(ChallengeRequestText, encoding);

    public static string ParseChallengeResponse(byte[] datagram, Encoding encoding)
    {
        var payload = ReadPayload(datagram, encoding).Trim();
        if (!payload.StartsWith(ChallengeResponsePrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new GoldSrcRconProtocolException("Unexpected GoldSrc RCON challenge response.");
        }

        var challenge = payload[ChallengeResponsePrefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(challenge))
        {
            throw new GoldSrcRconProtocolException("GoldSrc RCON challenge response did not include a challenge.");
        }

        return challenge;
    }

    public static byte[] BuildCommandRequest(
        string challenge,
        string password,
        string commandText,
        Encoding encoding)
    {
        EnsureSingleLine(challenge, nameof(challenge));
        EnsureSingleLine(password, nameof(password));
        EnsureSingleLine(commandText, nameof(commandText));

        if (password.Contains('"', StringComparison.Ordinal))
        {
            throw new GoldSrcRconProtocolException("GoldSrc RCON passwords containing double quotes are not supported.");
        }

        var payload = $"rcon {challenge} \"{password}\" {commandText}\n";
        return BuildDatagram(payload, encoding);
    }

    public static string ParseCommandResponse(byte[] datagram, Encoding encoding)
    {
        var payload = ReadPayload(datagram, encoding).Trim();
        if (payload.Contains(BadPasswordText, StringComparison.OrdinalIgnoreCase))
        {
            throw new GoldSrcRconAuthenticationException();
        }

        return payload.Length > 0 && payload[0] == 'l'
            ? payload[1..].Trim()
            : payload;
    }

    private static byte[] BuildDatagram(string payload, Encoding encoding)
    {
        var payloadBytes = encoding.GetBytes(payload);
        var datagram = new byte[HeaderLength + payloadBytes.Length];

        BinaryPrimitives.WriteInt32LittleEndian(datagram.AsSpan(0, HeaderLength), -1);
        payloadBytes.CopyTo(datagram.AsSpan(HeaderLength));

        return datagram;
    }

    private static string ReadPayload(byte[] datagram, Encoding encoding)
    {
        if (datagram.Length < HeaderLength)
        {
            throw new GoldSrcRconProtocolException("GoldSrc RCON response is too short.");
        }

        var header = BinaryPrimitives.ReadInt32LittleEndian(datagram.AsSpan(0, HeaderLength));
        if (header != -1)
        {
            throw new GoldSrcRconProtocolException("Unexpected GoldSrc RCON response header.");
        }

        return encoding.GetString(datagram.AsSpan(HeaderLength)).TrimEnd('\0');
    }

    private static void EnsureSingleLine(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        if (value.Contains('\r', StringComparison.Ordinal) || value.Contains('\n', StringComparison.Ordinal))
        {
            throw new GoldSrcRconProtocolException("GoldSrc RCON values must be single-line text.");
        }
    }
}
