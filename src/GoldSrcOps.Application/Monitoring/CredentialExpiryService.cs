using GoldSrcOps.Application.Common;

namespace GoldSrcOps.Application.Monitoring;

public sealed class CredentialExpiryService(ICredentialExpirySource source, IClock clock)
{
    public static IReadOnlyList<string> Keys { get; } = Array.AsReadOnly<string>(["grafana-metrics", "grafana-logs", "archive-reader", "archive-writer"]);

    public CredentialExpiryDto Read(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = clock.UtcNow;
        var metadata = source.Read().Take(5).ToArray();
        var entries = new List<CredentialExpiryEntryDto>(4);
        foreach (var key in Keys)
        {
            var matches = metadata.Where(item => string.Equals(item.Key, key, StringComparison.Ordinal)).Take(2).ToArray();
            var item = metadata.Length <= 4 && matches.Length == 1 ? matches[0] : null;
            var verified = item?.VerifiedAtUtc is { } checkedAt && checkedAt <= now ? checkedAt : (DateTimeOffset?)null;
            var expires = verified is not null ? item?.ExpiresAtUtc : null;
            var minute = verified is not null ? item?.ExpiresAtMinuteUtc : null;
            var deadline = expires;
            if (minute is not null)
            {
                if (expires is null && CredentialExpiryMinute.TryParse(minute, out var start))
                {
                    deadline = start;
                }
                else
                {
                    deadline = expires = null;
                    minute = null;
                }
            }

            var remaining = deadline - now;
            var state = remaining switch
            {
                null => "Unknown",
                { Ticks: <= 0 } => "Expired",
                _ when remaining <= TimeSpan.FromDays(7) => "Critical",
                _ when remaining <= TimeSpan.FromDays(30) => "Soon",
                _ => "Recorded"
            };
            int? days = remaining is { } duration ? Math.Max(0, (int)Math.Ceiling(duration.TotalDays)) : null;
            entries.Add(new CredentialExpiryEntryDto(key, state, expires, verified, days, minute));
        }

        return new CredentialExpiryDto(now, entries);
    }
}
