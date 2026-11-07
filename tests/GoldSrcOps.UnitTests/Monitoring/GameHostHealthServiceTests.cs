using AwesomeAssertions;
using GoldSrcOps.Application.Common;
using GoldSrcOps.Application.Monitoring;
using Moq;

namespace GoldSrcOps.UnitTests.Monitoring;

public sealed class GameHostHealthServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, "Unknown")]
    [InlineData(1, "NoWarnings")]
    [InlineData(2, "Pending")]
    [InlineData(3, "Firing")]
    [InlineData(4, "Unknown")]
    public async Task Maps_existing_policy_without_new_thresholds(double value, string expected)
    {
        var result = await ReadAsync([Sample(1, value), Sample(24, Now.ToUnixTimeSeconds())]);
        result.Components[0].State.Should().Be(expected);
        result.Components.Skip(1).Should().OnlyContain(static card => card.State == "Unknown");
    }

    [Theory]
    [InlineData(-181, "Stale")]
    [InlineData(-180, "NoWarnings")]
    [InlineData(1, "Unknown")]
    public async Task Observation_age_is_independent_from_query_sample_age(int age, string expected)
    {
        var result = await ReadAsync([Sample(1, 1), Sample(24, Now.AddSeconds(age).ToUnixTimeSeconds())]);
        result.Components[0].State.Should().Be(expected);
    }

    [Theory]
    [InlineData(-61)]
    [InlineData(1)]
    public async Task Rejects_old_or_future_query_samples(int age)
    {
        var result = await ReadAsync([Sample(1, 1) with { SampledAtUtc = Now.AddSeconds(age) }, Sample(24, Now.ToUnixTimeSeconds())]);
        result.Components[0].State.Should().Be("Unknown");
    }

    [Fact]
    public async Task Duplicate_and_nonfinite_values_do_not_become_healthy()
    {
        var result = await ReadAsync([Sample(1, 1), Sample(1, 1), Sample(24, Now.ToUnixTimeSeconds()), Sample(18, double.NaN)]);
        result.Components[0].State.Should().Be("Unknown");
        result.Values.Should().BeEmpty();
    }

    [Fact]
    public async Task Memory_remains_visible_when_cpu_is_missing_and_zero_values_are_retained()
    {
        var result = await ReadAsync([Sample(4, 1), Sample(27, Now.ToUnixTimeSeconds()), Sample(19, 0), Sample(14, 42) with { Role = "queue" }, Sample(14, 99) with { Role = "untrusted" }]);
        result.Components[3].State.Should().Be("NoWarnings");
        result.Components[4].State.Should().Be("Unknown");
        result.Values.Should().ContainSingle(static value => value.Metric == "swapUsedBytes" && value.Value == 0);
        result.Values.Should().NotContain(static value => value.Role == "untrusted");
    }

    [Fact]
    public async Task Cpu_observation_has_no_health_claim()
    {
        var result = await ReadAsync([Sample(5, 1), Sample(28, Now.ToUnixTimeSeconds())]);
        result.Components[4].State.Should().Be("Observed");
    }

    [Fact]
    public async Task Unavailable_source_returns_five_unknown_cards()
    {
        var result = await ReadAsync(samples: null);
        result.Components.Should().HaveCount(5).And.OnlyContain(static card => card.State == "Unknown");
    }

    private static GameHostHealthSample Sample(int panel, double value) => new(panel, string.Empty, value, Now);

    private static Task<GameHostHealthDto> ReadAsync(IReadOnlyList<GameHostHealthSample>? samples)
    {
        var source = new Mock<IGameHostHealthSource>();
        source.Setup(service => service.ReadAsync(Now, It.IsAny<CancellationToken>())).ReturnsAsync(samples);
        var clock = new Mock<IClock>();
        clock.SetupGet(static value => value.UtcNow).Returns(Now);
        var warningSource = new Mock<IGameHostWarningSource>();
        return new GameHostHealthService(source.Object, warningSource.Object, clock.Object).ReadAsync(CancellationToken.None);
    }
}
