using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using GoldSrcOps.Application.Servers;

namespace GoldSrcOps.Infrastructure.A2S;

public sealed class GoldSrcServerQueryClient : IGoldSrcServerQueryClient
{
    private static readonly byte[] QueryText = "Source Engine Query"u8.ToArray();

    private readonly Encoding _textEncoding;

    public GoldSrcServerQueryClient(Encoding textEncoding)
    {
        _textEncoding = textEncoding;
    }

    public async Task<GameServerInfo> QueryInfoAsync(GameServerEndpoint endpoint, CancellationToken cancellationToken)
    {
        var ipEndPoint = await ResolveEndpointAsync(endpoint.Host, endpoint.QueryPort, cancellationToken);

        using var udp = new UdpClient(AddressFamily.InterNetwork);
        var stopwatch = Stopwatch.StartNew();

        await SendInfoRequestAsync(udp, ipEndPoint, challenge: null, cancellationToken);
        var firstResponse = await ReceiveAsync(udp, endpoint.Timeout, cancellationToken);
        stopwatch.Stop();

        var firstPacket = A2SPacket.Parse(firstResponse.Buffer, _textEncoding, stopwatch.Elapsed);

        if (firstPacket is A2SChallengePacket challengePacket)
        {
            stopwatch.Restart();
            await SendInfoRequestAsync(udp, ipEndPoint, challengePacket.Challenge, cancellationToken);
            var challengedResponse = await ReceiveAsync(udp, endpoint.Timeout, cancellationToken);
            stopwatch.Stop();

            return A2SPacket.Parse(challengedResponse.Buffer, _textEncoding, stopwatch.Elapsed) switch
            {
                A2SInfoPacket infoPacket => infoPacket.Info,
                A2SChallengePacket => throw new InvalidOperationException("Server returned a second challenge instead of A2S_INFO."),
                _ => throw new InvalidOperationException("Unexpected A2S response packet.")
            };
        }

        return firstPacket switch
        {
            A2SInfoPacket infoPacket => infoPacket.Info,
            _ => throw new InvalidOperationException("Unexpected A2S response packet.")
        };
    }

    private static async Task<IPEndPoint> ResolveEndpointAsync(string host, int port, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var parsedAddress))
        {
            if (parsedAddress.AddressFamily != AddressFamily.InterNetwork)
            {
                throw new NotSupportedException("Only IPv4 endpoints are supported.");
            }

            return new IPEndPoint(parsedAddress, port);
        }

        var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cancellationToken);
        var address = addresses.FirstOrDefault()
            ?? throw new InvalidOperationException($"Host '{host}' did not resolve to an IPv4 address.");

        return new IPEndPoint(address, port);
    }

    private static async Task SendInfoRequestAsync(
        UdpClient udp,
        IPEndPoint endpoint,
        int? challenge,
        CancellationToken cancellationToken)
    {
        var request = BuildInfoRequest(challenge);
        await udp.SendAsync(request, request.Length, endpoint).WaitAsync(cancellationToken);
    }

    private static async Task<UdpReceiveResult> ReceiveAsync(
        UdpClient udp,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        return await udp.ReceiveAsync(linkedCts.Token);
    }

    private static byte[] BuildInfoRequest(int? challenge)
    {
        var challengeLength = challenge.HasValue ? sizeof(int) : 0;
        var request = new byte[sizeof(int) + sizeof(byte) + QueryText.Length + sizeof(byte) + challengeLength];
        var offset = 0;

        BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(offset, sizeof(int)), -1);
        offset += sizeof(int);

        request[offset++] = 0x54;

        QueryText.CopyTo(request.AsSpan(offset));
        offset += QueryText.Length;

        request[offset++] = 0x00;

        if (challenge.HasValue)
        {
            BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(offset, sizeof(int)), challenge.Value);
        }

        return request;
    }
}

internal abstract record A2SPacket
{
    public static A2SPacket Parse(byte[] datagram, Encoding textEncoding, TimeSpan latency)
    {
        var reader = new PacketReader(datagram, textEncoding);
        var header = reader.ReadInt32();

        if (header == -2)
        {
            throw new NotSupportedException("Split A2S packets are not supported yet.");
        }

        if (header != -1)
        {
            throw new InvalidOperationException($"Unexpected A2S packet header: {header}.");
        }

        var type = reader.ReadByte();

        return type switch
        {
            0x41 => new A2SChallengePacket(reader.ReadInt32()),
            0x49 => new A2SInfoPacket(ParseSourceInfo(reader, latency)),
            0x6D => new A2SInfoPacket(ParseGoldSrcInfo(reader, latency)),
            _ => throw new InvalidOperationException($"Unexpected A2S packet type: 0x{type:X2}.")
        };
    }

    private static GameServerInfo ParseSourceInfo(PacketReader reader, TimeSpan latency)
    {
        var protocol = reader.ReadByte();
        var name = reader.ReadString();
        var map = reader.ReadString();
        var folder = reader.ReadString();
        var game = reader.ReadString();

        _ = reader.ReadInt16();

        var players = reader.ReadByte();
        var maxPlayers = reader.ReadByte();
        var bots = reader.ReadByte();
        var serverType = (char)reader.ReadByte();
        var environment = (char)reader.ReadByte();
        var visibility = reader.ReadByte();
        var vac = reader.ReadByte();
        var version = reader.ReadString();

        return new GameServerInfo(
            "Source",
            name,
            map,
            folder,
            game,
            protocol,
            players,
            maxPlayers,
            bots,
            serverType,
            environment,
            visibility == 1,
            vac == 1,
            version,
            latency);
    }

    private static GameServerInfo ParseGoldSrcInfo(PacketReader reader, TimeSpan latency)
    {
        _ = reader.ReadString();

        var name = reader.ReadString();
        var map = reader.ReadString();
        var folder = reader.ReadString();
        var game = reader.ReadString();
        var players = reader.ReadByte();
        var maxPlayers = reader.ReadByte();
        var protocol = reader.ReadByte();
        var serverType = (char)reader.ReadByte();
        var environment = (char)reader.ReadByte();
        var visibility = reader.ReadByte();
        var isMod = reader.ReadByte();
        string? version = null;

        if (isMod == 1)
        {
            _ = reader.ReadString();
            _ = reader.ReadString();
            _ = reader.ReadByte();
            version = reader.ReadInt32().ToString();
            _ = reader.ReadInt32();
            _ = reader.ReadByte();
            _ = reader.ReadByte();
        }

        var vac = reader.ReadByte();
        var bots = reader.Remaining > 0 ? reader.ReadByte() : 0;

        return new GameServerInfo(
            "GoldSrc",
            name,
            map,
            folder,
            game,
            protocol,
            players,
            maxPlayers,
            bots,
            serverType,
            environment,
            visibility == 1,
            vac == 1,
            version,
            latency);
    }
}

internal sealed record A2SChallengePacket(int Challenge) : A2SPacket;

internal sealed record A2SInfoPacket(GameServerInfo Info) : A2SPacket;

internal ref struct PacketReader
{
    private readonly ReadOnlySpan<byte> _buffer;
    private readonly Encoding _encoding;
    private int _offset;

    public PacketReader(ReadOnlySpan<byte> buffer, Encoding encoding)
    {
        _buffer = buffer;
        _encoding = encoding;
        _offset = 0;
    }

    public int Remaining => _buffer.Length - _offset;

    public byte ReadByte()
    {
        EnsureAvailable(sizeof(byte));
        return _buffer[_offset++];
    }

    public short ReadInt16()
    {
        EnsureAvailable(sizeof(short));
        var value = BinaryPrimitives.ReadInt16LittleEndian(_buffer.Slice(_offset, sizeof(short)));
        _offset += sizeof(short);
        return value;
    }

    public int ReadInt32()
    {
        EnsureAvailable(sizeof(int));
        var value = BinaryPrimitives.ReadInt32LittleEndian(_buffer.Slice(_offset, sizeof(int)));
        _offset += sizeof(int);
        return value;
    }

    public string ReadString()
    {
        var start = _offset;

        while (_offset < _buffer.Length && _buffer[_offset] != 0)
        {
            _offset++;
        }

        if (_offset >= _buffer.Length)
        {
            throw new InvalidOperationException("A2S packet contains an unterminated string.");
        }

        var value = _encoding.GetString(_buffer[start.._offset]);
        _offset++;
        return value;
    }

    private void EnsureAvailable(int bytes)
    {
        if (Remaining < bytes)
        {
            throw new InvalidOperationException("A2S packet ended unexpectedly.");
        }
    }
}
