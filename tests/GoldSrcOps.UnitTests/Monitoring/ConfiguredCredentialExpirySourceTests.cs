using AwesomeAssertions;
using GoldSrcOps.Infrastructure.Monitoring;
using Microsoft.Extensions.Configuration;

namespace GoldSrcOps.UnitTests.Monitoring;

public sealed class ConfiguredCredentialExpirySourceTests
{
    [Theory]
    [InlineData("2031-04-05T06:17Z", null, true)]
    [InlineData("2031-04-05T06:17Z", "", true)]
    [InlineData("2031-04-05T06:17Z", "2031-04-05T06:17:00Z", false)]
    [InlineData("2031-04-05T06:17Z", "invalid-second-value", false)]
    [InlineData("2031-04-05T06:17:00Z", null, false)]
    [InlineData("2031-04-05T06:17+00:00", null, false)]
    [InlineData("2026-02-30T12:19Z", null, false)]
    [InlineData("2031-04-05T24:17Z", null, false)]
    [InlineData("2031-04-05T06:17z", null, false)]
    [InlineData(" 2031-04-05T06:17Z", null, false)]
    [InlineData("private-sentinel", null, false)]
    public void Only_unambiguous_strict_minutes_are_projected(string minute, string? seconds, bool valid)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["CredentialExpiry:ArchiveReader:ExpiresAtUtc"] = seconds,
            ["CredentialExpiry:ArchiveReader:ExpiresAtMinuteUtc"] = minute,
            ["CredentialExpiry:ArchiveReader:VerifiedAtUtc"] = "2026-11-08T12:00:00Z"
        }).Build();
        var entry = new ConfiguredCredentialExpirySource(config).Read()[2];
        entry.ExpiresAtUtc.Should().BeNull();
        entry.ExpiresAtMinuteUtc.Should().Be(valid ? minute : null);
        entry.VerifiedAtUtc.Should().NotBeNull();
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("private-secret-sentinel", false)]
    [InlineData("2027-02-01T12:00:00+00:00", false)]
    [InlineData("2027-02-30T12:00:00Z", false)]
    [InlineData("2027-02-01T12:00:00Z", true)]
    public void Only_exact_UTC_dates_in_fixed_sections_are_projected(string? value, bool valid)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["CredentialExpiry:GrafanaMetrics:ExpiresAtUtc"] = value,
            ["CredentialExpiry:GrafanaMetrics:VerifiedAtUtc"] = "2026-11-08T12:00:00Z",
            ["CredentialExpiry:private-secret-sentinel:ExpiresAtUtc"] = "2027-02-01T12:00:00Z"
        }).Build();
        var entries = new ConfiguredCredentialExpirySource(config).Read();
        entries.Should().HaveCount(4);
        entries[0].ExpiresAtUtc.HasValue.Should().Be(valid);
        entries[0].VerifiedAtUtc.Should().Be(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));
        entries.Skip(1).Should().OnlyContain(static item => item.ExpiresAtUtc == null && item.VerifiedAtUtc == null);
        entries.Should().NotContain(static item => item.Key == "private-secret-sentinel");
    }
}
