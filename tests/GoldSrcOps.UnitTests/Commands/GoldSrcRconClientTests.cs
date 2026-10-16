using System.Net;
using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using GoldSrcOps.Infrastructure.Commands;
using Moq;

namespace GoldSrcOps.UnitTests.Commands;

public sealed class GoldSrcRconClientTests
{
    private static readonly Encoding Encoding = Encoding.ASCII;
    private static readonly TimeSpan TestDrainInterval = TimeSpan.FromMilliseconds(50);

    [Fact]
    public async Task ExecuteAsync_completes_goldsrc_challenge_and_command_flow()
    {
        using var server = CreateServer();
        var serverEndpoint = GetLocalEndpoint(server);
        var serverTask = RunRconServerAsync(server, "lserver response\n");
        var sut = CreateSut();

        var response = await sut.ExecuteAsync(
            Request(serverEndpoint, timeout: TimeSpan.FromSeconds(2)),
            CancellationToken.None);

        response.Should().Be("server response");
        await serverTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ExecuteAsync_concatenates_multiple_response_datagrams_in_receive_order()
    {
        using var server = CreateServer();
        var serverEndpoint = GetLocalEndpoint(server);
        var serverTask = RunRconServerAsync(
            server,
            "lfirst ",
            "lsecond",
            "l\nthird\n");
        var sut = CreateSut();

        var response = await sut.ExecuteAsync(
            Request(serverEndpoint, timeout: TimeSpan.FromSeconds(2)),
            CancellationToken.None);

        response.Should().Be("first second\nthird");
        await serverTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ExecuteAsync_throws_timeout_when_server_does_not_respond()
    {
        using var server = CreateServer();
        var serverEndpoint = GetLocalEndpoint(server);
        var sut = CreateSut();

        var act = () => sut.ExecuteAsync(
            Request(serverEndpoint, timeout: TimeSpan.FromMilliseconds(50)),
            CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task ExecuteAsync_throws_timeout_when_first_command_response_does_not_arrive()
    {
        using var server = CreateServer();
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var timeout = TimeSpan.FromMilliseconds(200);
        var (timeProvider, expireDeadline) = CreateDeadlineTimeProvider(timeout);
        var sut = CreateSut(deadlineTimeProvider: timeProvider);
        var execution = sut.ExecuteAsync(Request(GetLocalEndpoint(server), timeout), watchdog.Token);

        await CompleteChallengeAsync(server, watchdog.Token);
        execution.IsCompleted.Should().BeFalse();
        expireDeadline();

        var completed = await Task.WhenAny(execution, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.Should().BeSameAs(execution, "the command deadline must finish the pending receive");
        var act = () => execution;
        await act.Should().ThrowAsync<TimeoutException>()
            .WithMessage("GoldSrc RCON response timed out.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_preserves_caller_cancellation(bool afterChallenge)
    {
        using var server = CreateServer();
        using var cancellationCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var timeout = TimeSpan.FromMilliseconds(200);
        var (timeProvider, _) = CreateDeadlineTimeProvider(timeout);
        var sut = CreateSut(deadlineTimeProvider: timeProvider);
        var execution = sut.ExecuteAsync(Request(GetLocalEndpoint(server), timeout), cancellationCts.Token);

        if (afterChallenge)
        {
            await CompleteChallengeAsync(server, cancellationCts.Token);
        }
        else
        {
            var challenge = await ReceiveAsync(server, cancellationCts.Token);
            challenge.Buffer.Should().Equal(GoldSrcRconProtocol.BuildChallengeRequest(Encoding));
        }

        execution.IsCompleted.Should().BeFalse();
        await cancellationCts.CancelAsync();

        var completed = await Task.WhenAny(execution, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.Should().BeSameAs(execution, "caller cancellation must finish the pending receive");
        var act = () => execution;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ExecuteAsync_rejects_non_print_command_response()
    {
        using var server = CreateServer();
        var serverEndpoint = GetLocalEndpoint(server);
        var serverTask = RunRconServerAsync(server, "unexpected response\n");
        var sut = CreateSut();

        var act = () => sut.ExecuteAsync(
            Request(serverEndpoint, timeout: TimeSpan.FromSeconds(2)),
            CancellationToken.None);

        await act.Should().ThrowAsync<GoldSrcRconProtocolException>()
            .WithMessage("*response type*");
        await serverTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ExecuteAsync_rejects_response_above_datagram_limit()
    {
        using var server = CreateServer();
        var serverEndpoint = GetLocalEndpoint(server);
        var serverTask = RunRconServerAsync(server, "lone", "ltwo", "lthree");
        var sut = CreateSut(maxResponseDatagrams: 2);

        var act = () => sut.ExecuteAsync(
            Request(serverEndpoint, timeout: TimeSpan.FromSeconds(2)),
            CancellationToken.None);

        await act.Should().ThrowAsync<GoldSrcRconProtocolException>()
            .WithMessage("*datagram limit*");
        await serverTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ExecuteAsync_rejects_response_above_wire_byte_limit()
    {
        using var server = CreateServer();
        var serverEndpoint = GetLocalEndpoint(server);
        var firstDatagram = Packet("lfirst");
        var secondDatagram = Packet("lsecond");
        var serverTask = RunRconServerAsync(server, "lfirst", "lsecond");
        var sut = CreateSut(maxResponseBytes: firstDatagram.Length + secondDatagram.Length - 1);

        var act = () => sut.ExecuteAsync(
            Request(serverEndpoint, timeout: TimeSpan.FromSeconds(2)),
            CancellationToken.None);

        await act.Should().ThrowAsync<GoldSrcRconProtocolException>()
            .WithMessage("*byte limit*");
        await serverTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ExecuteAsync_rejects_continuous_response_at_overall_deadline()
    {
        using var server = CreateServer();
        var serverEndpoint = GetLocalEndpoint(server);
        using var serverCts = new CancellationTokenSource();
        var serverTask = RunContinuousRconServerAsync(server, serverCts.Token);
        var sut = CreateSut(
            maxResponseDatagrams: 128,
            responseDrainInterval: TimeSpan.FromSeconds(1));

        var act = () => sut.ExecuteAsync(
            Request(serverEndpoint, timeout: TimeSpan.FromSeconds(1)),
            CancellationToken.None);

        try
        {
            await act.Should().ThrowAsync<GoldSrcRconProtocolException>()
                .WithMessage("*deadline*");
        }
        finally
        {
            await serverCts.CancelAsync();
            await serverTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task ExecuteAsync_ignores_datagrams_from_unconnected_endpoint()
    {
        using var server = CreateServer();
        using var unrelatedSender = CreateServer();
        var serverEndpoint = GetLocalEndpoint(server);
        var serverTask = RunEndpointIsolationServerAsync(server, unrelatedSender);
        var sut = CreateSut();

        var response = await sut.ExecuteAsync(
            Request(serverEndpoint, timeout: TimeSpan.FromSeconds(2)),
            CancellationToken.None);

        response.Should().Be("server response");
        await serverTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ExecuteAsync_preserves_complete_ascii_leaderboard_frame_across_datagrams()
    {
        using var server = CreateServer();
        var endpoint = GetLocalEndpoint(server);
        var now = DateTimeOffset.UtcNow;
        var stamp = now.ToUnixTimeSeconds();
        var name = Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes("Кириллица"));
        var serverTask = RunLeaderboardServerAsync(server, $"lGSLEADER 1 {stamp} 1\n",
            $"lGSLROW 1 2 120 2 {name}\n", $"lGSLEND 1 {stamp} 1\n");
        var response = await CreateSut().ExecuteAsync(
            new(endpoint.Address.ToString(), endpoint.Port, "secret", "goldsrcops_leaderboard_snapshot", TimeSpan.FromSeconds(2)),
            CancellationToken.None);
        var parsed = GoldSrcOps.Infrastructure.Monitoring.PublicLeaderboardProtocol.Parse(response, now);
        parsed!.Entries.Single().Name.Should().Be("Кириллица");
        await serverTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static GoldSrcRconClient CreateSut(
        int maxResponseDatagrams = 32,
        int maxResponseBytes = 64 * 1_024,
        TimeSpan? responseDrainInterval = null,
        TimeProvider? deadlineTimeProvider = null) =>
        new(
            Encoding,
            new GoldSrcRconOptions
            {
                ResponseDrainInterval = responseDrainInterval ?? TestDrainInterval,
                MaxResponseDatagrams = maxResponseDatagrams,
                MaxResponseBytes = maxResponseBytes
            },
            deadlineTimeProvider);

    private static (TimeProvider Provider, Action Expire) CreateDeadlineTimeProvider(TimeSpan timeout)
    {
        Action? expire = null;
        var provider = new Mock<TimeProvider>(MockBehavior.Strict);
        provider.Setup(value => value.CreateTimer(
                It.IsAny<TimerCallback>(), It.IsAny<object?>(), timeout, Timeout.InfiniteTimeSpan))
            .Callback<TimerCallback, object?, TimeSpan, TimeSpan>((callback, state, _, _) =>
                expire = () => callback(state))
            .Returns(Mock.Of<ITimer>());

        return (provider.Object, () =>
        {
            expire.Should().NotBeNull("the request must have a deadline timer");
            expire!();
        }
        );
    }

    private static GoldSrcRconRequest Request(IPEndPoint endpoint, TimeSpan timeout) =>
        new(
            endpoint.Address.ToString(),
            endpoint.Port,
            "secret",
            "say hello",
            timeout);

    private static UdpClient CreateServer() =>
        new(new IPEndPoint(IPAddress.Loopback, 0));

    private static IPEndPoint GetLocalEndpoint(UdpClient server) =>
        (IPEndPoint)server.Client.LocalEndPoint!;

    private static async Task RunLeaderboardServerAsync(UdpClient server, params string[] responseChunks)
    {
        var request = await CompleteChallengeAsync(server, CancellationToken.None, "goldsrcops_leaderboard_snapshot");
        foreach (var chunk in responseChunks)
        {
            await server.SendAsync(Packet(chunk), request.RemoteEndPoint, CancellationToken.None);
        }
    }

    private static async Task RunRconServerAsync(UdpClient server, params string[] responseChunks)
    {
        var commandRequest = await CompleteChallengeAsync(server, CancellationToken.None);

        foreach (var responseChunk in responseChunks)
        {
            var commandResponse = Packet(responseChunk);
            await server.SendAsync(
                commandResponse,
                commandRequest.RemoteEndPoint,
                CancellationToken.None);
        }
    }

    private static async Task RunContinuousRconServerAsync(
        UdpClient server,
        CancellationToken cancellationToken)
    {
        try
        {
            var commandRequest = await CompleteChallengeAsync(server, cancellationToken);

            while (true)
            {
                var commandResponse = Packet("lresponse chunk\n");
                await server.SendAsync(
                    commandResponse,
                    commandRequest.RemoteEndPoint,
                    cancellationToken);
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task RunEndpointIsolationServerAsync(
        UdpClient server,
        UdpClient unrelatedSender)
    {
        var challengeRequest = await ReceiveAsync(server, CancellationToken.None);

        await unrelatedSender.SendAsync(
            Packet("challenge rcon 987654321\n"),
            challengeRequest.RemoteEndPoint,
            CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(10));

        await server.SendAsync(
            Packet("challenge rcon 123456789\n"),
            challengeRequest.RemoteEndPoint,
            CancellationToken.None);

        var commandRequest = await ReceiveAsync(server, CancellationToken.None);
        commandRequest.Buffer.Should().Equal(
            GoldSrcRconProtocol.BuildCommandRequest(
                "123456789",
                "secret",
                "say hello",
                Encoding));

        await unrelatedSender.SendAsync(
            Packet("lforeign response\n"),
            commandRequest.RemoteEndPoint,
            CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(10));

        await server.SendAsync(
            Packet("lserver response\n"),
            commandRequest.RemoteEndPoint,
            CancellationToken.None);
    }

    private static async Task<UdpReceiveResult> CompleteChallengeAsync(
        UdpClient server,
        CancellationToken cancellationToken,
        string commandText = "say hello")
    {
        var challengeRequest = await ReceiveAsync(server, cancellationToken);
        challengeRequest.Buffer.Should().Equal(GoldSrcRconProtocol.BuildChallengeRequest(Encoding));

        await server.SendAsync(
            Packet("challenge rcon 123456789\n"),
            challengeRequest.RemoteEndPoint,
            cancellationToken);

        var commandRequest = await ReceiveAsync(server, cancellationToken);
        commandRequest.Buffer.Should().Equal(
            GoldSrcRconProtocol.BuildCommandRequest(
                "123456789",
                "secret",
                commandText,
                Encoding));

        return commandRequest;
    }

    private static async Task<UdpReceiveResult> ReceiveAsync(
        UdpClient server,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));
        return await server.ReceiveAsync(timeoutCts.Token);
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
