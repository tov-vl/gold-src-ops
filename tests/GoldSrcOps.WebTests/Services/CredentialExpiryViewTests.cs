using AwesomeAssertions;
using GoldSrcOps.Contracts.Monitoring;
using GoldSrcOps.Web.Services;

namespace GoldSrcOps.WebTests.Services;

public sealed class CredentialExpiryViewTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(-181, "Recorded", 90, false)]
    [InlineData(1, "Recorded", 90, false)]
    [InlineData(-180, "Recorded", 90, true)]
    [InlineData(0, "<private-sentinel>", 90, false)]
    [InlineData(0, "Recorded", 1, false)]
    [InlineData(0, "Recorded", 90, true)]
    public void Mixed_version_stale_future_or_inconsistent_data_cannot_claim_recorded_state(int age, string state, int days, bool valid)
    {
        var evaluated = Now.AddSeconds(age);
        var snapshot = new CredentialExpiryResponse(evaluated, [new CredentialExpiryEntryResponse("grafana-metrics", state, evaluated.AddDays(90), evaluated, days)]);
        var result = CredentialExpiryView.Create(snapshot, Now);
        result.Should().HaveCount(4);
        result[0].State.Should().Be(valid ? "Recorded" : "Unknown");
    }

    [Fact]
    public void Missing_duplicate_null_or_oversized_entries_are_unknown()
    {
        var item = new CredentialExpiryEntryResponse("grafana-metrics", "Recorded", Now.AddDays(90), Now, 90);
        foreach (var entries in new IReadOnlyList<CredentialExpiryEntryResponse>?[] { null, [null!], [item, item], [item, item, item, item, item] })
        {
            CredentialExpiryView.Create(new CredentialExpiryResponse(Now, entries!), Now)
                .Should().OnlyContain(static entry => entry.State == "Unknown" && entry.ExpiresAtUtc == null);
        }
    }
}
