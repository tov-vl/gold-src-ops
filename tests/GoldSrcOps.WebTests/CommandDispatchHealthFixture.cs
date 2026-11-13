using GoldSrcOps.Contracts.Monitoring;
using GoldSrcOps.Web.Services;

namespace GoldSrcOps.WebTests;

internal sealed class CommandDispatchHealthFixture : ICommandDispatchHealthClient
{
    public int Reads { get; private set; }
    public bool Unavailable { get; init; }

    public Task<CommandDispatchHealthResponse> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Reads++;
        return Unavailable ? Task.FromException<CommandDispatchHealthResponse>(new HttpRequestException("private-sentinel")) : Task.FromResult(Valid(DateTimeOffset.UtcNow));
    }

    public static CommandDispatchHealthResponse Valid(DateTimeOffset now) => new(now, "Running", now, now.AddSeconds(-6), now.AddSeconds(-5), 5, 660,
        "Available", now, [.. CommandDispatchHealthView.Definitions.Select(static item => new CommandDispatchWarningResponse(item.Name, "Inactive"))],
        now.AddSeconds(-5), LastFailureAtUtc: null, LastCycleDurationSeconds: 1, ConsecutiveFailures: 0, InProgress: false);
}
