using System.Globalization;
using GoldSrcOps.Contracts.Alerts;
using GoldSrcOps.Contracts.ProviderDelivery;

namespace GoldSrcOps.Web.Services;

internal static class IncidentDeliveryHealthView
{
    public static OperatorHealthOverview.Source Outbox(AlertDeliveryStatusResponse? value, DateTimeOffset now)
    {
        var valid = value is not null && ValidQueue(value.PendingCount, value.ProcessingCount, value.DeadLetterCount, value.OldestPendingAtUtc, value.ObservedAtUtc);
        var state = State(valid ? value?.ObservedAtUtc : null, now);
        return new OperatorHealthOverview.Source("incident-outbox", "Incident outbox", "/operator/alert-delivery", state, ValidTime(value?.ObservedAtUtc, now),
        [
            new OperatorHealthOverview.Fact("Dispatcher", state is "Available" ? value!.IsEnabled ? "Enabled" : "Disabled" : state),
            Count("Pending", value?.PendingCount, state), Count("Processing", value?.ProcessingCount, state),
            Count("Dead letters", value?.DeadLetterCount, state), Oldest(value?.OldestPendingAtUtc, state)
        ], Rules: null, "Incident notifications only. An empty queue or enabled dispatcher does not prove delivery. Prometheus to IRM/email is a separate channel.");
    }

    public static OperatorHealthOverview.Source Receiver(ProviderDeliveryStatusResponse? value, bool configured, DateTimeOffset now)
    {
        var valid = value is not null && value.ReceiverMode is "CatchUp" or "Live" &&
            ValidQueue(value.PendingCount, value.ProcessingCount, value.DeadLetterCount, value.OldestPendingAtUtc, value.ObservedAtUtc) &&
            value.UnreviewedDeadLetterCount >= 0 && value.UnreviewedDeadLetterCount <= value.DeadLetterCount;
        var state = configured ? State(valid ? value?.ObservedAtUtc : null, now) : "Disabled";
        return new OperatorHealthOverview.Source("incident-receiver", "Incident receiver", "/operator/provider-delivery", state, configured ? ValidTime(value?.ObservedAtUtc, now) : null,
        [
            new OperatorHealthOverview.Fact("Web receiver access", configured ? "Configured" : "Disabled"),
            new OperatorHealthOverview.Fact("Receiver mode", state is "Available" ? value!.ReceiverMode is "Live" ? "Live" : "Catch-up / sandbox" : configured ? state : "Not queried"),
            new OperatorHealthOverview.Fact("Provider worker", state is "Available" ? value!.IsEnabled ? "Enabled" : "Disabled" : configured ? state : "Not queried"),
            Count("Pending", value?.PendingCount, state), Count("Processing", value?.ProcessingCount, state),
            Count("Dead letters", value?.DeadLetterCount, state), Count("Unreviewed dead letters", value?.UnreviewedDeadLetterCount, state),
            Oldest(value?.OldestPendingAtUtc, state)
        ], Rules: null, "CatchUp suppresses new provider work; existing worker state is independent. Disabled Web access sends no receiver request. Queue state does not prove email receipt; IRM/email is separate.");
    }

    private static bool ValidQueue(long pending, long processing, long deadLetters, DateTimeOffset? oldest, DateTimeOffset observed) =>
        pending >= 0 && processing >= 0 && deadLetters >= 0 &&
        (pending == 0 ? oldest is null : oldest >= DateTimeOffset.UnixEpoch && oldest <= observed);

    private static string State(DateTimeOffset? time, DateTimeOffset now) =>
        time is null || time < DateTimeOffset.UnixEpoch || time > now ? "Unknown" : now - time > TimeSpan.FromSeconds(60) ? "Stale" : "Available";

    private static DateTimeOffset? ValidTime(DateTimeOffset? time, DateTimeOffset now) => time >= DateTimeOffset.UnixEpoch && time <= now ? time : null;

    private static OperatorHealthOverview.Fact Count(string label, long? value, string state) =>
        new(label, state switch { "Available" => value!.Value.ToString(CultureInfo.InvariantCulture), "Disabled" => "Not queried", _ => state },
            state is "Available" && value > 0 ? "Backlog" : null);

    private static OperatorHealthOverview.Fact Oldest(DateTimeOffset? value, string state) =>
        new("Oldest pending", state switch { "Available" => value?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) ?? "None", "Disabled" => "Not queried", _ => state });
}
