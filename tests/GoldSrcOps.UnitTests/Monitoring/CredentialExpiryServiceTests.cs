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

    private static CredentialExpiryService Create(IReadOnlyList<CredentialExpiryMetadata> metadata)
    {
        var source = new Mock<ICredentialExpirySource>();
        source.Setup(static value => value.Read()).Returns(metadata);
        var clock = new Mock<IClock>();
        clock.SetupGet(static value => value.UtcNow).Returns(Now);
        return new CredentialExpiryService(source.Object, clock.Object);
    }
}
