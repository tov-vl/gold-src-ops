using GoldSrcOps.Application.Common;

namespace GoldSrcOps.Application.Monitoring;

public sealed class PublicLeaderboardStore(IClock clock)
{
    private readonly System.Threading.Lock gate = new();
    private PublicLeaderboardSnapshot? snapshot;
    private bool lastAttemptSucceeded;

    public void Publish(PublicLeaderboardSnapshot value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (gate)
        {
            snapshot = value with { Entries = Array.AsReadOnly(value.Entries.ToArray()) };
            lastAttemptSucceeded = true;
        }
    }

    public void RecordFailure(bool sourceUnavailable = false)
    {
        lock (gate)
        {
            lastAttemptSucceeded = false;
            if (sourceUnavailable)
            {
                snapshot = null;
            }
        }
    }

    public PublicLeaderboardProjection Read()
    {
        lock (gate)
        {
            if (snapshot is null || clock.UtcNow - snapshot.CapturedAtUtc > TimeSpan.FromDays(1))
            {
                return new("unavailable", null, []);
            }

            var state = lastAttemptSucceeded && clock.UtcNow - snapshot.CapturedAtUtc <= TimeSpan.FromMinutes(3)
                ? "fresh" : "stale";
            return new(state, snapshot.CapturedAtUtc, snapshot.Entries);
        }
    }
}
