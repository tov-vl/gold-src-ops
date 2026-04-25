using System.Text;
using GoldSrcOps.Infrastructure.A2S;

namespace GoldSrcOps.UnitTests.A2S;

public sealed class A2SPacketParserTests
{
    [Fact]
    public void Parse_reads_source_info_packet()
    {
        var datagram = Hex(
            """
            FFFFFFFF4930435320312E3620546573740064655F64757374320063737472696B6500
            436F756E7465722D537472696B65000A000C2001646C0001312E312E322E372F5374
            64696F00
            """);
        var latency = TimeSpan.FromMilliseconds(42);

        var packet = A2SPacket.Parse(datagram, Encoding.UTF8, latency);

        var infoPacket = Assert.IsType<A2SInfoPacket>(packet);
        var info = infoPacket.Info;
        Assert.Equal("Source", info.ResponseFormat);
        Assert.Equal("CS 1.6 Test", info.Name);
        Assert.Equal("de_dust2", info.Map);
        Assert.Equal("cstrike", info.Folder);
        Assert.Equal("Counter-Strike", info.Game);
        Assert.Equal(48, info.Protocol);
        Assert.Equal(12, info.Players);
        Assert.Equal(32, info.MaxPlayers);
        Assert.Equal(1, info.Bots);
        Assert.Equal('d', info.ServerType);
        Assert.Equal('l', info.Environment);
        Assert.False(info.IsPrivate);
        Assert.True(info.HasVac);
        Assert.Equal("1.1.2.7/Stdio", info.Version);
        Assert.Equal(latency, info.Latency);
    }

    [Fact]
    public void Parse_reads_goldsrc_info_packet()
    {
        var datagram = Hex(
            """
            FFFFFFFF6D3132372E302E302E313A323730313500476F6C64537263205465737400
            64655F64757374320063737472696B6500436F756E7465722D537472696B65000A20
            30646C0101000000D20400002E16000000010102
            """);
        var latency = TimeSpan.FromMilliseconds(64);

        var packet = A2SPacket.Parse(datagram, Encoding.UTF8, latency);

        var infoPacket = Assert.IsType<A2SInfoPacket>(packet);
        var info = infoPacket.Info;
        Assert.Equal("GoldSrc", info.ResponseFormat);
        Assert.Equal("GoldSrc Test", info.Name);
        Assert.Equal("de_dust2", info.Map);
        Assert.Equal("cstrike", info.Folder);
        Assert.Equal("Counter-Strike", info.Game);
        Assert.Equal(48, info.Protocol);
        Assert.Equal(10, info.Players);
        Assert.Equal(32, info.MaxPlayers);
        Assert.Equal(2, info.Bots);
        Assert.Equal('d', info.ServerType);
        Assert.Equal('l', info.Environment);
        Assert.True(info.IsPrivate);
        Assert.True(info.HasVac);
        Assert.Equal("1234", info.Version);
        Assert.Equal(latency, info.Latency);
    }

    [Fact]
    public void Parse_reads_challenge_packet()
    {
        var datagram = Hex("FFFFFFFF4178563412");

        var packet = A2SPacket.Parse(datagram, Encoding.UTF8, TimeSpan.Zero);

        var challengePacket = Assert.IsType<A2SChallengePacket>(packet);
        Assert.Equal(0x12345678, challengePacket.Challenge);
    }

    private static byte[] Hex(string value)
    {
        return Convert.FromHexString(value.Replace(" ", string.Empty).ReplaceLineEndings(string.Empty));
    }
}
