using GoldSrcOps.Application.Common;

namespace GoldSrcOps.Application.Monitoring;

public sealed class GameHostHealthService(IGameHostHealthSource source, IGameHostWarningSource warningSource, IClock clock)
{
    private static readonly string[] Components = ["agent", "backup", "storage", "memory", "cpu"];
    private static readonly string[] Metrics = ["queuePending", "queueInFlight", "queueDeadLetter", "spoolFiles", "backupAgeSeconds", "backupSource", "weeklyAttempt", "weeklyMissed", "diskAvailableBytes", "diskAvailableRatio", "inodesAvailable", "inodesAvailableRatio", "memoryAvailableBytes", "swapUsedBytes", "cpuBusyRatio", "cpuStealRatio"];
    private static readonly string[] Roles = ["queue", "spool", "players", "backup"];

    public async Task<GameHostHealthDto> ReadAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var samplesTask = source.ReadAsync(now, cancellationToken);
        var warningsTask = warningSource.ReadAsync(cancellationToken);
        await Task.WhenAll(samplesTask, warningsTask).ConfigureAwait(false);
        var samples = await samplesTask.ConfigureAwait(false);
        var warnings = await warningsTask.ConfigureAwait(false);
        now = clock.UtcNow;
        var cards = new List<GameHostHealthCardDto>(5);
        for (var panel = 1; panel <= Components.Length; panel++)
        {
            var observation = Single(samples, panel + 23, string.Empty, now);
            DateTimeOffset? observedAt = observation is >= 0 and <= 253402300799
                ? DateTimeOffset.FromUnixTimeSeconds((long)observation.Value) : null;
            var stale = observedAt < now.AddSeconds(-180);
            var validObservation = observedAt >= now.AddSeconds(-180) && observedAt <= now;
            var statusValue = validObservation ? Single(samples, panel, string.Empty, now) : null;
            var status = stale ? "Stale" : statusValue switch
            {
                1 => panel == 5 ? "Observed" : "NoWarnings",
                2 when panel != 5 => "Pending",
                3 when panel != 5 => "Firing",
                _ => "Unknown"
            };
            cards.Add(new GameHostHealthCardDto(Components[panel - 1], status, observedAt));
        }

        var values = new List<GameHostHealthValueDto>();
        for (var panel = 6; panel <= 21; panel++)
        {
            foreach (var role in panel is >= 14 and <= 17 ? Roles : [string.Empty])
            {
                var value = Single(samples, panel, role, now);
                if (value is >= 0)
                {
                    values.Add(new GameHostHealthValueDto(Metrics[panel - 6], role, value.Value));
                }
            }
        }

        return new GameHostHealthDto(now, cards, values, GameHostWarningsProjection.Create(warnings, now));
    }

    private static double? Single(IReadOnlyList<GameHostHealthSample>? samples, int panel, string role, DateTimeOffset now)
    {
        var matches = samples?.Where(sample => sample.Panel == panel && string.Equals(sample.Role, role, StringComparison.Ordinal)).Take(2).ToArray();
        return matches is { Length: 1 } && double.IsFinite(matches[0].Value) &&
            matches[0].SampledAtUtc >= now.AddSeconds(-60) && matches[0].SampledAtUtc <= now
            ? matches[0].Value : null;
    }
}
