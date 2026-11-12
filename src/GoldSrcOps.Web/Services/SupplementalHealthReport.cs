using System.Globalization;
using System.Text;
using GoldSrcOps.Contracts.Alerts;
using GoldSrcOps.Contracts.Monitoring;
using GoldSrcOps.Contracts.ProviderDelivery;

namespace GoldSrcOps.Web.Services;

internal static class SupplementalHealthReport
{
    internal sealed record Snapshots(CredentialExpiryResponse? Credentials, AlertDeliveryStatusResponse? Outbox,
        ProviderDeliveryStatusResponse? Receiver, bool ReceiverConfigured);

    public static void Append(StringBuilder output, Snapshots? snapshots, DateTimeOffset generatedAt)
    {
        var credentials = snapshots?.Credentials;
        var time = credentials?.EvaluatedAtUtc;
        var state = time is null || time < DateTimeOffset.UnixEpoch || time > generatedAt ? "Unknown" :
            generatedAt - time > TimeSpan.FromSeconds(180) ? "Stale" : "Available";
        output.AppendLine();
        output.AppendLine("Credential expiry");
        output.Append("Credential snapshot: ").AppendLine(state);
        output.Append("Credential snapshot evaluated: ").AppendLine(Time(time >= DateTimeOffset.UnixEpoch && time <= generatedAt ? time : null));
        var entries = CredentialExpiryView.Create(state is "Available" ? credentials : null, generatedAt);
        foreach (var purpose in CredentialExpiryView.Catalog)
        {
            var entry = entries.Single(item => string.Equals(item.Key, purpose.Key, StringComparison.Ordinal));
            output.Append(purpose.Purpose).Append(": ").AppendLine(CredentialExpiryView.StateLabel(entry.State, entry.ExpiresAtMinuteUtc is not null));
            output.Append("Provider: ").Append(purpose.Provider).Append("; consumer: ").AppendLine(purpose.Consumer);
            output.Append("Recorded expiry: ").AppendLine(entry.ExpiresAtMinuteUtc is { } minute ? minute.Replace('T', ' ')[..^1] + " UTC" : Time(entry.ExpiresAtUtc));
            output.Append("Precision: ").AppendLine(entry.ExpiresAtMinuteUtc is not null ? "minute (conservative start)" : entry.ExpiresAtUtc is not null ? "second" : "Unknown");
            output.Append("Last verified: ").AppendLine(Time(entry.VerifiedAtUtc));
            output.Append("Days remaining: ").AppendLine(entry.RemainingDays?.ToString(CultureInfo.InvariantCulture) ?? "Unknown");
        }

        output.AppendLine("Recorded dates are not live provider checks or proof that credentials work. Unknown/Stale does not establish validity or no expiry.");
        output.Append("Details: ").AppendLine(CredentialExpiryView.Path);
        AppendDelivery(output, IncidentDeliveryHealthView.Outbox(snapshots?.Outbox, generatedAt));
        AppendDelivery(output, IncidentDeliveryHealthView.Receiver(snapshots?.Receiver, snapshots?.ReceiverConfigured ?? false, generatedAt));
    }

    private static void AppendDelivery(StringBuilder output, OperatorHealthOverview.Source source)
    {
        output.AppendLine();
        output.AppendLine(source.Title);
        output.Append(source.Title).Append(" snapshot: ").AppendLine(source.SnapshotState);
        output.Append("Source snapshot time: ").AppendLine(Time(source.EvaluatedAtUtc));
        foreach (var fact in source.Facts)
        {
            output.Append(fact.Label).Append(": ").AppendLine(fact.Value);
        }

        output.AppendLine(source.Note);
        output.Append("Details: ").AppendLine(source.DetailsPath);
    }

    private static string Time(DateTimeOffset? value) => value?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) ?? "Unknown";
}
