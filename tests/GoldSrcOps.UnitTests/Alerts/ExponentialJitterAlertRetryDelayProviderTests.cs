using AwesomeAssertions;
using GoldSrcOps.Application.Alerts;

namespace GoldSrcOps.UnitTests.Alerts;

public sealed class ExponentialJitterAlertRetryDelayProviderTests
{
    private static readonly AlertDispatcherSettings Settings = new(
        claimTimeout: TimeSpan.FromSeconds(30),
        maxAttempts: 10,
        baseRetryDelay: TimeSpan.FromSeconds(4),
        maximumRetryDelay: TimeSpan.FromSeconds(60),
        processedRetentionPeriod: TimeSpan.FromDays(30),
        cleanupBatchSize: 100);

    [Theory]
    [InlineData(1, 4, 4)]
    [InlineData(2, 4, 8)]
    [InlineData(3, 8, 16)]
    [InlineData(10, 30, 60)]
    public void GetDelay_applies_bounded_exponential_jitter(
        int attemptCount,
        int minimumSeconds,
        int maximumSeconds)
    {
        var sut = new ExponentialJitterAlertRetryDelayProvider(Settings);

        var delays = Enumerable.Range(0, 20)
            .Select(_ => sut.GetDelay(attemptCount))
            .ToArray();

        delays.Should().OnlyContain(delay =>
            delay >= TimeSpan.FromSeconds(minimumSeconds) &&
            delay <= TimeSpan.FromSeconds(maximumSeconds));
    }

    [Fact]
    public void GetDelay_rejects_nonpositive_attempt_counts()
    {
        var sut = new ExponentialJitterAlertRetryDelayProvider(Settings);

        var act = () => sut.GetDelay(0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
