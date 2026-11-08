using AwesomeAssertions;
using GoldSrcOps.Infrastructure.Monitoring;
using Microsoft.Extensions.Configuration;

namespace GoldSrcOps.UnitTests.Monitoring;

public sealed class ConfiguredCredentialExpirySourceTests
{
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
