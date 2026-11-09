using AwesomeAssertions;
using GoldSrcOps.Application.Common;
using GoldSrcOps.Application.Monitoring;
using GoldSrcOps.Application.Telemetry;
using GoldSrcOps.Infrastructure.Monitoring;
using GoldSrcOps.UnitTests.Helpers;
using Moq;

namespace GoldSrcOps.UnitTests.Monitoring;

public sealed class CredentialExpiryMetricsTests
{
    [Theory]
    [InlineData(-1, "Expired")]
    [InlineData(0, "Expired")]
    [InlineData(1, "Critical")]
    [InlineData(604800, "Critical")]
    [InlineData(604801, "Soon")]
    [InlineData(2592000, "Soon")]
    [InlineData(2592001, "Recorded")]
    public async Task Collection_uses_the_existing_exact_and_minute_boundary_policy(int seconds, string expected)
    {
        var clock = new FixtureClock();
        var deadline = clock.UtcNow;
        clock.UtcNow = deadline.AddSeconds(-seconds);
        var source = new Mock<ICredentialExpirySource>();
        source.Setup(static value => value.Read()).Returns([
            new CredentialExpiryMetadata("grafana-metrics", deadline, clock.UtcNow.AddDays(-1)),
            new CredentialExpiryMetadata("archive-reader", null, clock.UtcNow.AddDays(-1), "2026-11-09T12:00Z")]);
        var service = new CredentialExpiryService(source.Object, clock);
        using var metrics = new CredentialExpiryMetrics(service);
        using var collector = new MetricsCollector(GoldSrcOpsMetrics.MeterName, item => ReferenceEquals(item.Meter.Scope, service));
        await metrics.StartAsync(CancellationToken.None);
        collector.CollectObservableMetrics();
        var rows = collector.Measurements.Where(static item => string.Equals(item.Name, "goldsrcops.credential_expiry.state", StringComparison.Ordinal)).ToArray();
        rows.Should().HaveCount(20);
        rows.Should().OnlyContain(static row => row.Tags.Count == 2 && row.Tags.ContainsKey("purpose") && row.Tags.ContainsKey("state"));
        rows.Select(static item => item.Value).Should().OnlyContain(static value => value >= 0 && value <= 1 && Math.Truncate(value).Equals(value));
        rows.Where(static item => item.Value > 0).Should().HaveCount(4);
        rows.Where(static item => item.Value > 0 && string.Equals((string)item.Tags["purpose"]!, "grafana-metrics", StringComparison.Ordinal))
            .Should().ContainSingle().Which.Tags["state"].Should().Be(expected);
        rows.Where(static item => item.Value > 0 && string.Equals((string)item.Tags["purpose"]!, "archive-reader", StringComparison.Ordinal))
            .Should().ContainSingle().Which.Tags["state"].Should().Be(expected);
        rows.Where(static item => item.Value > 0 && string.Equals((string)item.Tags["purpose"]!, "archive-writer", StringComparison.Ordinal))
            .Should().ContainSingle().Which.Tags["state"].Should().Be("Unknown");
        collector.Measurements.Single(static item => string.Equals(item.Name, "goldsrcops.credential_expiry.observed_timestamp", StringComparison.Ordinal))
            .Value.Should().Be((clock.UtcNow - DateTimeOffset.UnixEpoch).TotalSeconds);
        await metrics.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void Repeated_collection_replaces_the_active_state_without_exposing_source_fields()
    {
        var clock = new FixtureClock();
        var source = new FixtureSource { Metadata = [new CredentialExpiryMetadata("grafana-logs", clock.UtcNow.AddDays(90), clock.UtcNow)] };
        var service = new CredentialExpiryService(source, clock);
        using var metrics = new CredentialExpiryMetrics(service);
        using var collector = new MetricsCollector(GoldSrcOpsMetrics.MeterName, item => ReferenceEquals(item.Meter.Scope, service));
        collector.CollectObservableMetrics();
        source.Metadata = [new CredentialExpiryMetadata("grafana-logs", clock.UtcNow.AddDays(90), clock.UtcNow.AddSeconds(1)),
            new CredentialExpiryMetadata("private-sentinel", clock.UtcNow, clock.UtcNow)];
        collector.CollectObservableMetrics();
        var last = collector.Measurements.TakeLast(20).ToArray();
        last.Where(static item => item.Value > 0).Should().HaveCount(4)
            .And.OnlyContain(static item => string.Equals((string)item.Tags["state"]!, "Unknown", StringComparison.Ordinal));
        last.SelectMany(static item => item.Tags.Values).Should().NotContain("private-sentinel");
        source.Metadata = [new CredentialExpiryMetadata("grafana-logs", clock.UtcNow.AddDays(60), clock.UtcNow)];
        collector.CollectObservableMetrics();
        collector.Measurements.TakeLast(20).Where(static item => string.Equals((string)item.Tags["purpose"]!, "grafana-logs", StringComparison.Ordinal) && item.Value > 0)
            .Should().ContainSingle().Which.Tags["state"].Should().Be("Recorded");
    }

    private sealed class FixtureClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class FixtureSource : ICredentialExpirySource
    {
        public IReadOnlyList<CredentialExpiryMetadata> Metadata { get; set; } = [];

        public IReadOnlyList<CredentialExpiryMetadata> Read() => Metadata;
    }
}
