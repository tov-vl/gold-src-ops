using System.Globalization;
using GoldSrcOps.Contracts.Monitoring;

namespace GoldSrcOps.Web.Services;

internal static class CredentialExpiryView
{
    public const string Path = "/operator/credential-expiry";

    public static IReadOnlyList<(string Key, string Provider, string Purpose, string Consumer)> Catalog { get; } = Array.AsReadOnly<(string Key, string Provider, string Purpose, string Consumer)>(
    [
        ("grafana-metrics", "Grafana Cloud", "Read availability metrics", "Availability evidence export"),
        ("grafana-logs", "Grafana Cloud", "Read availability logs", "Availability evidence export"),
        ("archive-reader", "Backblaze B2", "Read availability archive", "Availability evidence recovery"),
        ("archive-writer", "Backblaze B2", "Write availability archive", "Availability evidence export")
    ]);

    public static IReadOnlyList<CredentialExpiryEntryResponse> Create(CredentialExpiryResponse? snapshot, DateTimeOffset now)
    {
        var fresh = snapshot?.EvaluatedAtUtc >= now.AddSeconds(-180) && snapshot.EvaluatedAtUtc <= now;
        IEnumerable<CredentialExpiryEntryResponse?>? input = snapshot?.Entries;
        var entries = input?.Take(5).ToArray();
        return [.. Catalog.Select(item =>
        {
            var matches = entries?.Where(value => string.Equals(value?.Key, item.Key, StringComparison.Ordinal)).Take(2).ToArray();
            var candidate = fresh && entries is { Length: <= 4 } && matches is { Length: 1 } ? matches[0] : null;
            if (candidate is { State: "Unknown", ExpiresAtUtc: null, ExpiresAtMinuteUtc: null, RemainingDays: null } && candidate.VerifiedAtUtc <= snapshot!.EvaluatedAtUtc)
            {
                return candidate;
            }

            if (candidate is not { VerifiedAtUtc: { } verified } || verified > snapshot!.EvaluatedAtUtc || ReadDeadline(candidate) is not { } expires)
            {
                return new CredentialExpiryEntryResponse(item.Key, "Unknown", ExpiresAtUtc: null, VerifiedAtUtc: null, RemainingDays: null);
            }

            var remaining = expires - snapshot.EvaluatedAtUtc;
            var expected = remaining.Ticks <= 0 ? "Expired" : remaining <= TimeSpan.FromDays(7) ? "Critical" : remaining <= TimeSpan.FromDays(30) ? "Soon" : "Recorded";
            var days = Math.Max(0, (int)Math.Ceiling(remaining.TotalDays));
            return string.Equals(candidate.State, expected, StringComparison.Ordinal) && candidate.RemainingDays == days
                ? candidate : new CredentialExpiryEntryResponse(item.Key, "Unknown", ExpiresAtUtc: null, VerifiedAtUtc: null, RemainingDays: null);
        })];
    }

    private static DateTimeOffset? ReadDeadline(CredentialExpiryEntryResponse entry)
    {
        if (entry.ExpiresAtMinuteUtc is null)
        {
            return entry.ExpiresAtUtc;
        }

        return entry.ExpiresAtUtc is null && entry.ExpiresAtMinuteUtc is { Length: 17 } &&
            DateTimeOffset.TryParseExact(entry.ExpiresAtMinuteUtc, "yyyy-MM-dd'T'HH:mm'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var start) ? start : null;
    }

    public static string StateLabel(string state, bool minutePrecision = false) => state switch
    {
        "Expired" when minutePrecision => "Recorded minute reached (conservative)",
        "Expired" => "Recorded deadline passed",
        "Critical" => "Due within 7 days",
        "Soon" => "Due within 30 days",
        "Recorded" => "Recorded deadline beyond 30 days",
        _ => "Unknown"
    };
}
