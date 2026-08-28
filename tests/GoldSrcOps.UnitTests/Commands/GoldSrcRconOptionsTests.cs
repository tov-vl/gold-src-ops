using AwesomeAssertions;
using GoldSrcOps.Infrastructure.Commands;
using Microsoft.Extensions.Configuration;

namespace GoldSrcOps.UnitTests.Commands;

public sealed class GoldSrcRconOptionsTests
{
    [Fact]
    public void FromConfiguration_uses_compatible_defaults_when_section_is_absent()
    {
        var configuration = new ConfigurationBuilder().Build();

        var result = GoldSrcRconOptions.FromConfiguration(configuration);

        result.Timeout.Should().Be(TimeSpan.FromSeconds(3));
        result.MaxResponseLength.Should().Be(2_000);
        result.ResponseDrainInterval.Should().Be(TimeSpan.FromMilliseconds(100));
        result.MaxResponseDatagrams.Should().Be(32);
        result.MaxResponseBytes.Should().Be(64 * 1_024);
    }

    [Fact]
    public void FromConfiguration_reads_valid_response_collection_limits()
    {
        var configuration = CreateConfiguration(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Rcon:TimeoutMilliseconds"] = "5000",
            ["Rcon:MaxResponseLength"] = "1500",
            ["Rcon:ResponseDrainMilliseconds"] = "250",
            ["Rcon:MaxResponseDatagrams"] = "8",
            ["Rcon:MaxResponseBytes"] = "32768"
        });

        var result = GoldSrcRconOptions.FromConfiguration(configuration);

        result.Timeout.Should().Be(TimeSpan.FromSeconds(5));
        result.MaxResponseLength.Should().Be(1_500);
        result.ResponseDrainInterval.Should().Be(TimeSpan.FromMilliseconds(250));
        result.MaxResponseDatagrams.Should().Be(8);
        result.MaxResponseBytes.Should().Be(32_768);
    }

    [Theory]
    [InlineData("ResponseDrainMilliseconds", "9")]
    [InlineData("ResponseDrainMilliseconds", "1001")]
    [InlineData("ResponseDrainMilliseconds", "not-an-integer")]
    [InlineData("MaxResponseDatagrams", "0")]
    [InlineData("MaxResponseDatagrams", "257")]
    [InlineData("MaxResponseBytes", "4")]
    [InlineData("MaxResponseBytes", "1048577")]
    public void FromConfiguration_rejects_invalid_response_collection_limits(string key, string value)
    {
        var configuration = CreateConfiguration(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [$"Rcon:{key}"] = value
        });

        var act = () => GoldSrcRconOptions.FromConfiguration(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*Rcon:{key}*");
    }

    private static IConfiguration CreateConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
}
