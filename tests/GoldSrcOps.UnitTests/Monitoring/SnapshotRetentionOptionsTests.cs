using AwesomeAssertions;
using GoldSrcOps.Infrastructure.Monitoring;
using Microsoft.Extensions.Configuration;

namespace GoldSrcOps.UnitTests.Monitoring;

public sealed class SnapshotRetentionOptionsTests
{
    [Fact]
    public void FromConfiguration_uses_safe_defaults_when_section_is_absent()
    {
        var configuration = new ConfigurationBuilder().Build();

        var result = SnapshotRetentionOptions.FromConfiguration(configuration);

        result.Enabled.Should().BeTrue();
        result.RetentionPeriod.Should().Be(TimeSpan.FromDays(30));
        result.CleanupInterval.Should().Be(TimeSpan.FromMinutes(5));
        result.BatchSize.Should().Be(1_000);
    }

    [Fact]
    public void FromConfiguration_reads_valid_values()
    {
        var configuration = CreateConfiguration(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["SnapshotRetention:Enabled"] = "false",
            ["SnapshotRetention:RetentionDays"] = "90",
            ["SnapshotRetention:CleanupIntervalSeconds"] = "600",
            ["SnapshotRetention:BatchSize"] = "2500"
        });

        var result = SnapshotRetentionOptions.FromConfiguration(configuration);

        result.Enabled.Should().BeFalse();
        result.RetentionPeriod.Should().Be(TimeSpan.FromDays(90));
        result.CleanupInterval.Should().Be(TimeSpan.FromMinutes(10));
        result.BatchSize.Should().Be(2_500);
    }

    [Theory]
    [InlineData("Enabled", "sometimes")]
    [InlineData("RetentionDays", "0")]
    [InlineData("RetentionDays", "3651")]
    [InlineData("CleanupIntervalSeconds", "9")]
    [InlineData("CleanupIntervalSeconds", "86401")]
    [InlineData("BatchSize", "0")]
    [InlineData("BatchSize", "10001")]
    [InlineData("BatchSize", "many")]
    public void FromConfiguration_rejects_invalid_values(string key, string value)
    {
        var configuration = CreateConfiguration(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [$"SnapshotRetention:{key}"] = value
        });

        var act = () => SnapshotRetentionOptions.FromConfiguration(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*SnapshotRetention:{key}*");
    }

    private static IConfiguration CreateConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
}
