using GoldSrcOps.Contracts.Alerts;
using GoldSrcOps.Contracts.Monitoring;
using GoldSrcOps.Contracts.ProviderDelivery;
using GoldSrcOps.Web.Services;

namespace GoldSrcOps.WebTests;

internal static class OperatorHealthOverviewFixtures
{
    public static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    public static AlertDeliveryStatusResponse Outbox(DateTimeOffset now) => new(IsEnabled: false, 2, 1, 3, now.AddMinutes(-3), now.AddSeconds(-4));
    public static ProviderDeliveryStatusResponse Receiver(DateTimeOffset now) => new(IsEnabled: false, "CatchUp", 1, 0, 2, 1, now.AddMinutes(-2), now.AddSeconds(-5));

    public static GameHostHealthResponse GameHost(DateTimeOffset now) => new(now,
    [
        new GameHostHealthComponentResponse("agent", "NoWarnings", now.AddSeconds(-10)), new GameHostHealthComponentResponse("backup", "NoWarnings", now.AddSeconds(-20)),
        new GameHostHealthComponentResponse("storage", "Firing", now.AddSeconds(-30)), new GameHostHealthComponentResponse("memory", "NoWarnings", now.AddSeconds(-40)), new GameHostHealthComponentResponse("cpu", "Observed", now.AddSeconds(-50))
    ], [], new GameHostWarningsResponse("Available", now.AddSeconds(-5),
        [new GameHostWarningResponse("GameHostMemoryLow", "private-sentinel", "Firing", "private-sentinel")]));

    public static ArchiveHealthResponse Archive(DateTimeOffset now) => new(now, "Available", now.AddMinutes(-2), HasScheduledSuccess: true,
        now.AddHours(-4), 14400, "Available", now.AddSeconds(-5),
        [.. ArchiveHealthView.Definitions.Select(static item => new ArchiveWarningResponse(item.Name, item.Name is "ArchiveScheduleDelayed" ? "Firing" : "Inactive"))]);

    public static PollingHealthResponse Polling(DateTimeOffset now) => new(now, "Running", now.AddSeconds(-5), now.AddSeconds(-11),
        now.AddSeconds(-10), 10, 370, "Available", now.AddSeconds(-6),
        [.. PollingHealthView.Definitions.Select(static item => new PollingWarningResponse(item.Name, "Inactive"))]);

    public static CredentialExpiryResponse Credentials(DateTimeOffset now) => new(now,
    [
        new CredentialExpiryEntryResponse("grafana-metrics", "Recorded", now.AddDays(45), now.AddDays(-1), 45),
        new CredentialExpiryEntryResponse("grafana-logs", "Soon", now.AddDays(14), now.AddDays(-1), 14),
        new CredentialExpiryEntryResponse("archive-reader", "Critical", now.AddDays(7), now.AddDays(-1), 7),
        new CredentialExpiryEntryResponse("archive-writer", "Expired", ExpiresAtUtc: null, now.AddDays(-1), 0, "2000-01-01T12:19Z")
    ]);

    public sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
