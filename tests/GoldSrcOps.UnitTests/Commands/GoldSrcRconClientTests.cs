using System.Net;
using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using GoldSrcOps.Infrastructure.Commands;

namespace GoldSrcOps.UnitTests.Commands;

public sealed class GoldSrcRconClientTests
{
    private static readonly Encoding Encoding = Encoding.ASCII;

    [Fact]
    public async Task ExecuteAsync_completes_goldsrc_challenge_and_command_flow()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var serverEndpoint = (IPEndPoint)server.Client.LocalEndPoint!;
        var serverTask = RunRconServerAsync(server);
        var sut = new GoldSrcRconClient(Encoding);

        var response = await sut.ExecuteAsync(
            new GoldSrcRconRequest(
                "127.0.0.1",
                serverEndpoint.Port,
                "secret",
                "say hello",
                TimeSpan.FromSeconds(2)),
            CancellationToken.None);

        response.Should().Be("server response");
        await serverTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ExecuteAsync_throws_timeout_when_server_does_not_respond()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var serverEndpoint = (IPEndPoint)server.Client.LocalEndPoint!;
        var sut = new GoldSrcRconClient(Encoding);

        var act = () => sut.ExecuteAsync(
            new GoldSrcRconRequest(
                "127.0.0.1",
                serverEndpoint.Port,
                "secret",
                "say hello",
                TimeSpan.FromMilliseconds(50)),
            CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();
    }

    private static async Task RunRconServerAsync(UdpClient server)
    {
        var challengeRequest = await ReceiveAsync(server);
        challengeRequest.Buffer.Should().Equal(GoldSrcRconProtocol.BuildChallengeRequest(Encoding));

        var challengeResponse = Packet("challenge rcon 123456789\n");
        await server.SendAsync(challengeResponse, challengeResponse.Length, challengeRequest.RemoteEndPoint);

        var commandRequest = await ReceiveAsync(server);
        commandRequest.Buffer.Should().Equal(
            GoldSrcRconProtocol.BuildCommandRequest("123456789", "secret", "say hello", Encoding));

        var commandResponse = Packet("lserver response\n");
        await server.SendAsync(commandResponse, commandResponse.Length, commandRequest.RemoteEndPoint);
    }

    private static async Task<UdpReceiveResult> ReceiveAsync(UdpClient server)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        return await server.ReceiveAsync(cts.Token);
    }

    private static byte[] Packet(string payload)
    {
        var payloadBytes = Encoding.GetBytes(payload);
        var packet = new byte[4 + payloadBytes.Length];

        packet[0] = 0xFF;
        packet[1] = 0xFF;
        packet[2] = 0xFF;
        packet[3] = 0xFF;
        payloadBytes.CopyTo(packet.AsSpan(4));

        return packet;
    }
}
