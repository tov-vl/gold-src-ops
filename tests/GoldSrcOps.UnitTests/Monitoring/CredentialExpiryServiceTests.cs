using AwesomeAssertions;
using GoldSrcOps.Application.Common;
using GoldSrcOps.Application.Monitoring;
using Moq;

namespace GoldSrcOps.UnitTests.Monitoring;

public sealed class CredentialExpiryServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(-1, "Expired", 0)]
    [InlineData(0, "Expired", 0)]
    [InlineData(1, "Critical", 1)]
    [InlineData(604800, "Critical", 7)]
    [InlineData(604801, "Soon", 8)]
    [InlineData(2592000, "Soon", 30)]
    [InlineData(2592001, "Recorded", 31)]
    public void Exact_deadline_and_warning_boundaries_do_not_use_rounded_days(int seconds, string state, int days)
    {
        var result = Create([new CredentialExpiryMetadata("grafana-metrics", Now.AddSeconds(seconds), Now.AddDays(-1))]).Read(CancellationToken.None);
        result.EvaluatedAtUtc.Should().Be(Now);
        var entry = result.Entries[0];
        entry.State.Should().Be(state);
        entry.RemainingDays.Should().Be(days);
        entry.ExpiresAtUtc.Should().Be(Now.AddSeconds(seconds));
        result.Entries.Should().HaveCount(4);
    }

    [Fact]
    public void Unknown_metadata_cannot_claim_unlimited_or_valid_access()
    {
        var result = Create([
            new CredentialExpiryMetadata("grafana-metrics", Now.AddDays(90), VerifiedAtUtc: null),
            new CredentialExpiryMetadata("grafana-logs", Now.AddDays(90), Now.AddSeconds(1)),
            new CredentialExpiryMetadata("archive-reader", ExpiresAtUtc: null, Now),
            new CredentialExpiryMetadata("unrecognized-private-value", Now, Now)
        ]).Read(CancellationToken.None);
        result.Entries.Should().OnlyContain(static item => item.State == "Unknown" && item.ExpiresAtUtc == null && item.RemainingDays == null);
        result.Entries[2].VerifiedAtUtc.Should().Be(Now);
        result.Entries.Should().NotContain(static item => item.Key == "unrecognized-private-value");
    }

    [Fact]
    public void Duplicates_and_oversized_sources_fail_closed()
    {
        var item = new CredentialExpiryMetadata("grafana-metrics", Now.AddDays(90), Now);
        Create([item, item]).Read(CancellationToken.None).Entries[0].State.Should().Be("Unknown");
        Create([item, item with { Key = "a" }, item with { Key = "b" }, item with { Key = "c" }, item with { Key = "d" }])
            .Read(CancellationToken.None).Entries.Should().OnlyContain(static entry => entry.State == "Unknown");
    }

    [Fact]
    public void Caller_cancellation_prevents_source_read()
    {
        var source = new Mock<ICredentialExpirySource>(MockBehavior.Strict);
        var clock = new Mock<IClock>();
        var service = new CredentialExpiryService(source.Object, clock.Object);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var token = cancellation.Token;
        var action = () => service.Read(token);
        action.Should().Throw<OperationCanceledException>();
        source.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(-59, "Expired", 0)]
    [InlineData(0, "Expired", 0)]
    [InlineData(1, "Critical", 1)]
    [InlineData(604800, "Critical", 7)]
    [InlineData(604801, "Soon", 8)]
    [InlineData(2592000, "Soon", 30)]
    [InlineData(2592001, "Recorded", 31)]
    public void Minute_deadline_uses_conservative_start_without_publishing_invented_seconds(int remainingSeconds, string state, int days)
    {
        var evaluated = Now.AddSeconds(-remainingSeconds);
        var result = Create([new CredentialExpiryMetadata("archive-reader", ExpiresAtUtc: null, evaluated.AddDays(-1), "2026-11-08T12:00Z")], evaluated)
            .Read(CancellationToken.None).Entries[2];
        result.State.Should().Be(state);
        result.RemainingDays.Should().Be(days);
        result.ExpiresAtUtc.Should().BeNull();
        result.ExpiresAtMinuteUtc.Should().Be("2026-11-08T12:00Z");
    }

    [Theory]
    [InlineData("private-sentinel", false, false)]
    [InlineData("2026-11-15T12:00:00Z", false, false)]
    [InlineData("2026-11-15T12:00Z", true, false)]
    [InlineData("2026-11-15T12:00Z", false, true)]
    public void Ambiguous_malformed_or_future_verified_minute_metadata_is_scrubbed(string minute, bool secondDate, bool futureVerification)
    {
        var item = new CredentialExpiryMetadata("archive-reader", secondDate ? Now.AddDays(7) : null, futureVerification ? Now.AddSeconds(1) : Now, minute);
        var entry = Create([item]).Read(CancellationToken.None).Entries[2];
        entry.State.Should().Be("Unknown");
        entry.ExpiresAtUtc.Should().BeNull();
        entry.ExpiresAtMinuteUtc.Should().BeNull();
        entry.RemainingDays.Should().BeNull();
    }

    private static CredentialExpiryService Create(IReadOnlyList<CredentialExpiryMetadata> metadata, DateTimeOffset? now = null)
    {
        var source = new Mock<ICredentialExpirySource>();
        source.Setup(static value => value.Read()).Returns(metadata);
        var clock = new Mock<IClock>();
        clock.SetupGet(static value => value.UtcNow).Returns(now ?? Now);
        return new CredentialExpiryService(source.Object, clock.Object);
    }
}
