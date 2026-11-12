using GoldSrcOps.Contracts.Monitoring;
using GoldSrcOps.Web.Services;

namespace GoldSrcOps.WebTests;

internal sealed class RetentionHealthFixture : IRetentionHealthClient
{
    public int Reads { get; private set; }
    public bool Unavailable { get; init; }

    public Task<RetentionHealthResponse> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Reads++;
        return Unavailable ? Task.FromException<RetentionHealthResponse>(new HttpRequestException("private-sentinel")) : Task.FromResult(Valid(DateTimeOffset.UtcNow));
    }

    public static RetentionHealthResponse Valid(DateTimeOffset now) => new(now, "Running", now, now.AddSeconds(-6), now.AddSeconds(-5), 5, 660,
        "Available", now, [.. RetentionHealthView.Definitions.Select(static item => new RetentionWarningResponse(item.Name, "Inactive"))],
        now.AddSeconds(-5), LastFailureAtUtc: null, LastCycleDurationSeconds: 1, ConsecutiveFailures: 0, InProgress: false);
}
