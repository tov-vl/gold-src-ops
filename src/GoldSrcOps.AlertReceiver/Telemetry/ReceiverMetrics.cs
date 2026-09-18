using System.Diagnostics.Metrics;
using GoldSrcOps.AlertReceiver.ProviderDelivery;

namespace GoldSrcOps.AlertReceiver.Telemetry;

internal static class ReceiverMetrics
{
    public const string MeterName = "GoldSrcOps.AlertReceiver";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<int> DeliveryAttempts = Meter.CreateCounter<int>(
        "goldsrcops.receiver.provider_delivery.attempts");
    private static readonly Counter<int> RecoveredClaims = Meter.CreateCounter<int>(
        "goldsrcops.receiver.provider_delivery.claims_recovered");
    private static readonly Counter<int> ProcessedDeleted = Meter.CreateCounter<int>(
        "goldsrcops.receiver.provider_delivery.processed_deleted");
    private static long _pending;
    private static long _processing;
    private static long _deadLetters;
    private static double _oldestPendingAgeSeconds;

    static ReceiverMetrics()
    {
        Meter.CreateObservableGauge(
            "goldsrcops.receiver.provider_delivery.pending",
            () => Interlocked.Read(ref _pending));
        Meter.CreateObservableGauge(
            "goldsrcops.receiver.provider_delivery.processing",
            () => Interlocked.Read(ref _processing));
        Meter.CreateObservableGauge(
            "goldsrcops.receiver.provider_delivery.dead_letters",
            () => Interlocked.Read(ref _deadLetters));
        Meter.CreateObservableGauge(
            "goldsrcops.receiver.provider_delivery.oldest_pending_age",
            () => Volatile.Read(ref _oldestPendingAgeSeconds),
            unit: "s");
    }

    public static void RecordAttempt(string result) =>
        DeliveryAttempts.Add(1, new KeyValuePair<string, object?>("result", result));

    public static void RecordRecovery(int recovered) => AddIfPositive(RecoveredClaims, recovered);

    public static void RecordProcessedDeleted(int deleted) => AddIfPositive(ProcessedDeleted, deleted);

    public static void UpdateStatistics(ProviderOutboxStatistics statistics, DateTimeOffset observedAtUtc)
    {
        Interlocked.Exchange(ref _pending, statistics.PendingCount);
        Interlocked.Exchange(ref _processing, statistics.ProcessingCount);
        Interlocked.Exchange(ref _deadLetters, statistics.DeadLetterCount);
        Volatile.Write(
            ref _oldestPendingAgeSeconds,
            Math.Max(0, statistics.OldestPendingAtUtc is { } oldest
                ? (observedAtUtc - oldest).TotalSeconds
                : 0));
    }

    private static void AddIfPositive(Counter<int> counter, int value)
    {
        if (value > 0)
        {
            counter.Add(value);
        }
    }
}
