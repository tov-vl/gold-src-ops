using AwesomeAssertions;
using GoldSrcOps.Api.Hosting;
using Microsoft.Extensions.Configuration;

namespace GoldSrcOps.UnitTests.Api;

public sealed class PublicServerJoinOptionsTests
{
    [Fact]
    public void FromConfiguration_disables_an_absent_public_server()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>(StringComparer.Ordinal));

        var result = PublicServerJoinOptions.FromConfiguration(configuration);

        result.Enabled.Should().BeFalse();
    }

    [Fact]
    public void FromConfiguration_reads_a_complete_public_server()
    {
        var serverId = Guid.NewGuid();
        var configuration = BuildConfiguration(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PublicServerJoin:ServerId"] = serverId.ToString("D"),
            ["PublicServerJoin:Name"] = "GoldSrcOps Public Classic",
            ["PublicServerJoin:Host"] = "PLAY.Example.Test",
            ["PublicServerJoin:Port"] = "27015"
        });

        var result = PublicServerJoinOptions.FromConfiguration(configuration);

        result.Should().BeEquivalentTo(new
        {
            Enabled = true,
            ServerId = serverId,
            Name = "GoldSrcOps Public Classic",
            Host = "play.example.test",
            Port = 27015
        });
    }

    [Theory]
    [InlineData("ServerId", "not-a-guid")]
    [InlineData("Name", " Public server")]
    [InlineData("Host", "https://play.example.test")]
    [InlineData("Port", "0")]
    [InlineData("Port", "65536")]
    public void FromConfiguration_rejects_an_invalid_partial_or_unsafe_value(string key, string value)
    {
        var configurationValues = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PublicServerJoin:ServerId"] = Guid.NewGuid().ToString("D"),
            ["PublicServerJoin:Name"] = "GoldSrcOps Public Classic",
            ["PublicServerJoin:Host"] = "play.example.test",
            ["PublicServerJoin:Port"] = "27015",
            [$"PublicServerJoin:{key}"] = value
        };
        var configuration = BuildConfiguration(configurationValues);

        var action = () => PublicServerJoinOptions.FromConfiguration(configuration);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage($"*PublicServerJoin:{key}*");
    }

    private static IConfiguration BuildConfiguration(
        IReadOnlyDictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
}
