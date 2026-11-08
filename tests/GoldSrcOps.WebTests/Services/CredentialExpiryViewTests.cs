using System.Text.Json;
using AwesomeAssertions;
using GoldSrcOps.Contracts.Monitoring;
using GoldSrcOps.Web.Services;

namespace GoldSrcOps.WebTests.Services;

public sealed class CredentialExpiryViewTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("2026-11-15T12:00Z", "Critical", 7, false, true)]
    [InlineData("2026-11-15T12:00Z", "Critical", 7, true, false)]
    [InlineData("2026-11-15T12:00Z", "Recorded", 7, false, false)]
    [InlineData("2026-11-15T12:00:00Z", "Critical", 7, false, false)]
    [InlineData("private-sentinel", "Critical", 7, false, false)]
    [InlineData("2026-11-15T12:00Z", "Unknown", 0, false, false)]
    public void Minute_precision_is_validated_independently_of_provider_text(string minute, string state, int days, bool hasSeconds, bool valid)
    {
        var snapshot = new CredentialExpiryResponse(Now, [new CredentialExpiryEntryResponse("archive-reader", state, hasSeconds ? Now.AddDays(7) : null, Now, days, minute)]);
        var entry = CredentialExpiryView.Create(snapshot, Now)[2];
        entry.State.Should().Be(valid ? "Critical" : "Unknown");
        entry.ExpiresAtUtc.Should().BeNull();
        entry.ExpiresAtMinuteUtc.Should().Be(valid ? minute : null);
        CredentialExpiryView.StateLabel("Expired", minutePrecision: true).Should().Be("Recorded minute reached (conservative)");
    }

    [Fact]
    public void New_web_accepts_old_second_payload_and_old_contract_cannot_infer_seconds_for_new_minute_payload()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        const string oldJson = "{\"evaluatedAtUtc\":\"2026-11-08T12:00:00Z\",\"entries\":[{\"key\":\"grafana-metrics\",\"state\":\"Recorded\",\"expiresAtUtc\":\"2027-02-06T12:00:00Z\",\"verifiedAtUtc\":\"2026-11-08T12:00:00Z\",\"remainingDays\":90}]}";
        var old = JsonSerializer.Deserialize<CredentialExpiryResponse>(oldJson, options);
        CredentialExpiryView.Create(old, Now)[0].State.Should().Be("Recorded");
        var newEntry = new CredentialExpiryEntryResponse("archive-reader", "Critical", ExpiresAtUtc: null, Now, 7, "2026-11-15T12:00Z");
        var legacy = JsonSerializer.Deserialize<LegacyEntry>(JsonSerializer.Serialize(newEntry, options), options);
        legacy!.ExpiresAtUtc.Should().BeNull();
        legacy.State.Should().Be("Critical");
    }

    private sealed record LegacyEntry(string Key, string State, DateTimeOffset? ExpiresAtUtc, DateTimeOffset? VerifiedAtUtc, int? RemainingDays);

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
