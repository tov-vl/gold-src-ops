using System.Text;
using AwesomeAssertions;
using GoldSrcOps.Infrastructure.Commands;

namespace GoldSrcOps.UnitTests.Commands;

public sealed class GoldSrcRconProtocolTests
{
    private static readonly Encoding Encoding = Encoding.ASCII;

    [Fact]
    public void BuildChallengeRequest_creates_goldsrc_udp_packet()
    {
        var request = GoldSrcRconProtocol.BuildChallengeRequest(Encoding);

        request.Should().Equal(Packet("challenge rcon\n"));
    }

    [Fact]
    public void ParseChallengeResponse_reads_challenge_token()
    {
        var challenge = GoldSrcRconProtocol.ParseChallengeResponse(
            Packet("challenge rcon 123456789\n"),
            Encoding);

        challenge.Should().Be("123456789");
    }

    [Fact]
    public void BuildCommandRequest_quotes_password_and_appends_command()
    {
        var request = GoldSrcRconProtocol.BuildCommandRequest(
            "123456789",
            "secret",
            "say hello",
            Encoding);

        request.Should().Equal(Packet("rcon 123456789 \"secret\" say hello\n"));
    }

    [Fact]
    public void ParseCommandResponse_strips_goldsrc_response_prefix()
    {
        var response = GoldSrcRconProtocol.ParseCommandResponse(
            Packet("lserver response\n"),
            Encoding);

        response.Should().Be("server response");
    }

    [Fact]
    public void ParseCommandResponseChunk_preserves_chunk_boundaries_until_final_normalization()
    {
        var firstChunk = GoldSrcRconProtocol.ParseCommandResponseChunk(
            Packet("l first "),
            Encoding);
        var secondChunk = GoldSrcRconProtocol.ParseCommandResponseChunk(
            Packet("lsecond\n "),
            Encoding);

        firstChunk.Should().Be(" first ");
        secondChunk.Should().Be("second\n ");
        GoldSrcRconProtocol.NormalizeCommandResponse(firstChunk + secondChunk)
            .Should().Be("first second");
    }

    [Theory]
    [InlineData("Bad rcon_password.\n")]
    [InlineData("lBad rcon_password.\n")]
    public void ParseCommandResponse_detects_bad_password_response(string payload)
    {
        var act = () => GoldSrcRconProtocol.ParseCommandResponse(
            Packet(payload),
            Encoding);

        act.Should().Throw<GoldSrcRconAuthenticationException>();
    }

    [Fact]
    public void ParseCommandResponse_rejects_non_print_response()
    {
        var act = () => GoldSrcRconProtocol.ParseCommandResponse(
            Packet("unexpected response\n"),
            Encoding);

        act.Should().Throw<GoldSrcRconProtocolException>()
            .WithMessage("*response type*");
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
