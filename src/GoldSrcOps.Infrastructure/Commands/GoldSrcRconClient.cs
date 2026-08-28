using System.Net;
using System.Net.Sockets;
using System.Text;

namespace GoldSrcOps.Infrastructure.Commands;

internal sealed class GoldSrcRconClient : IGoldSrcRconClient
{
    private const string TimeoutMessage = "GoldSrc RCON response timed out.";

    private readonly Encoding _textEncoding;
    private readonly GoldSrcRconOptions _options;

    public GoldSrcRconClient(Encoding textEncoding, GoldSrcRconOptions options)
    {
        ArgumentNullException.ThrowIfNull(textEncoding);
        ArgumentNullException.ThrowIfNull(options);

        _textEncoding = textEncoding;
        _options = options;
    }

    public async Task<string> ExecuteAsync(GoldSrcRconRequest request, CancellationToken cancellationToken)
    {
        using var deadlineCts = new CancellationTokenSource(request.Timeout);
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadlineCts.Token);

        try
        {
            var endpoint = await ResolveEndpointAsync(request.Host, request.Port, operationCts.Token);

            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Connect(endpoint);

            await udp.SendAsync(
                GoldSrcRconProtocol.BuildChallengeRequest(_textEncoding),
                operationCts.Token);

            var challengeResponse = await udp.ReceiveAsync(operationCts.Token);
            var challenge = GoldSrcRconProtocol.ParseChallengeResponse(
                challengeResponse.Buffer,
                _textEncoding);

            var commandRequest = GoldSrcRconProtocol.BuildCommandRequest(
                challenge,
                request.Password,
                request.CommandText,
                _textEncoding);

            await udp.SendAsync(commandRequest, operationCts.Token);
            var firstCommandResponse = await udp.ReceiveAsync(operationCts.Token);

            return await ReceiveCommandResponseAsync(
                udp,
                firstCommandResponse.Buffer,
                deadlineCts,
                cancellationToken);
        }
        catch (OperationCanceledException) when (
            deadlineCts.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(TimeoutMessage);
        }
    }

    private static async Task<IPEndPoint> ResolveEndpointAsync(
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var parsedAddress))
        {
            if (parsedAddress.AddressFamily != AddressFamily.InterNetwork)
            {
                throw new NotSupportedException("Only IPv4 endpoints are supported.");
            }

            return new IPEndPoint(parsedAddress, port);
        }

        var addresses = await Dns.GetHostAddressesAsync(
            host,
            AddressFamily.InterNetwork,
            cancellationToken);
        var address = addresses.FirstOrDefault()
            ?? throw new InvalidOperationException($"Host '{host}' did not resolve to an IPv4 address.");

        return new IPEndPoint(address, port);
    }

    private async Task<string> ReceiveCommandResponseAsync(
        UdpClient udp,
        byte[] firstDatagram,
        CancellationTokenSource deadlineCts,
        CancellationToken cancellationToken)
    {
        var response = new StringBuilder();
        var datagramCount = 0;
        var responseBytes = 0;

        AppendResponseChunk(firstDatagram, response, ref datagramCount, ref responseBytes);

        while (true)
        {
            using var drainCts = new CancellationTokenSource(_options.ResponseDrainInterval);
            using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                deadlineCts.Token,
                drainCts.Token);

            try
            {
                var commandResponse = await udp.ReceiveAsync(receiveCts.Token);
                AppendResponseChunk(
                    commandResponse.Buffer,
                    response,
                    ref datagramCount,
                    ref responseBytes);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (deadlineCts.IsCancellationRequested)
            {
                throw new GoldSrcRconProtocolException(
                    "GoldSrc RCON response did not become quiet before the command deadline expired.");
            }
            catch (OperationCanceledException) when (drainCts.IsCancellationRequested)
            {
                return GoldSrcRconProtocol.NormalizeCommandResponse(response.ToString());
            }
        }
    }

    private void AppendResponseChunk(
        byte[] datagram,
        StringBuilder response,
        ref int datagramCount,
        ref int responseBytes)
    {
        if (datagramCount >= _options.MaxResponseDatagrams)
        {
            throw new GoldSrcRconProtocolException(
                "GoldSrc RCON response exceeded the configured datagram limit.");
        }

        if (datagram.Length > _options.MaxResponseBytes - responseBytes)
        {
            throw new GoldSrcRconProtocolException(
                "GoldSrc RCON response exceeded the configured byte limit.");
        }

        response.Append(GoldSrcRconProtocol.ParseCommandResponseChunk(datagram, _textEncoding));
        datagramCount++;
        responseBytes += datagram.Length;
    }
}
