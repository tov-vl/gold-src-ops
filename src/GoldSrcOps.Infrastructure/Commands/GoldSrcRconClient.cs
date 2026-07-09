using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace GoldSrcOps.Infrastructure.Commands;

internal sealed class GoldSrcRconClient : IGoldSrcRconClient
{
    private readonly Encoding _textEncoding;

    public GoldSrcRconClient(Encoding textEncoding)
    {
        _textEncoding = textEncoding;
    }

    public async Task<string> ExecuteAsync(GoldSrcRconRequest request, CancellationToken cancellationToken)
    {
        var endpoint = await ResolveEndpointAsync(request.Host, request.Port, cancellationToken);

        using var udp = new UdpClient(AddressFamily.InterNetwork);

        var stopwatch = Stopwatch.StartNew();

        await udp.SendAsync(GoldSrcRconProtocol.BuildChallengeRequest(_textEncoding), endpoint, cancellationToken);
        var challengeResponse = await ReceiveAsync(udp, request.Timeout, cancellationToken);
        var challenge = GoldSrcRconProtocol.ParseChallengeResponse(challengeResponse.Buffer, _textEncoding);

        var commandRequest = GoldSrcRconProtocol.BuildCommandRequest(
            challenge,
            request.Password,
            request.CommandText,
            _textEncoding);

        await udp.SendAsync(commandRequest, endpoint, cancellationToken);
        var commandResponse = await ReceiveAsync(
            udp,
            RemainingTimeout(request.Timeout, stopwatch.Elapsed),
            cancellationToken);

        return GoldSrcRconProtocol.ParseCommandResponse(commandResponse.Buffer, _textEncoding);
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

    private static async Task<UdpReceiveResult> ReceiveAsync(
        UdpClient udp,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            return await udp.ReceiveAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("GoldSrc RCON response timed out.");
        }
    }

    private static TimeSpan RemainingTimeout(TimeSpan timeout, TimeSpan elapsed)
    {
        var remaining = timeout - elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1);
    }
}
