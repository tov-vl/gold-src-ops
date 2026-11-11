using System.Globalization;
using System.Text;
using GoldSrcOps.Contracts.Monitoring;

namespace GoldSrcOps.Web.Services;

internal static class GameHostHealthReport
{
    private static readonly (string Key, string Label)[] Components =
    [
        ("agent", "Agent / queue"), ("backup", "Player backup"), ("storage", "Disk / inode"),
        ("memory", "RAM / swap"), ("cpu", "Sampled CPU")
    ];

    private static readonly (string Key, string Label)[] Metrics =
    [
        ("queuePending", "Queue pending"), ("queueInFlight", "Queue in-flight"), ("queueDeadLetter", "Queue dead-letter"),
        ("spoolFiles", "Spool ready + processing"), ("backupAgeSeconds", "Backup copy age (seconds)"),
        ("backupSource", "Backup copy source"), ("weeklyAttempt", "Last weekly attempt"), ("weeklyMissed", "Missed weekly slot"),
        ("diskAvailableBytes", "Disk available (bytes)"), ("diskAvailableRatio", "Disk available (ratio)"),
        ("inodesAvailable", "Inodes available"), ("inodesAvailableRatio", "Inodes available (ratio)"),
        ("memoryAvailableBytes", "RAM available (bytes)"), ("swapUsedBytes", "Swap used (bytes)"),
        ("cpuBusyRatio", "CPU busy (ratio)"), ("cpuStealRatio", "CPU steal (ratio)")
    ];

    private static readonly string[] StorageRoles = ["queue", "spool", "players", "backup"];

    public static string Create(GameHostHealthResponse? snapshot, DateTimeOffset generatedAt, ArchiveHealthResponse? archive = null)
    {
        var output = new StringBuilder();
        output.AppendLine("GoldSrcOps game-host health report");
        output.Append("Generated: ").AppendLine(Time(generatedAt));
        output.Append("Snapshot evaluated: ").AppendLine(Time(snapshot?.EvaluatedAtUtc));
        output.Append("Snapshot age (seconds): ").AppendLine(Age(snapshot?.EvaluatedAtUtc, generatedAt));
        var snapshotState = Freshness(snapshot?.EvaluatedAtUtc, generatedAt, 180);
        output.Append("Snapshot: ").AppendLine(snapshotState);
        output.AppendLine("Download fetches independent game-host and Archive snapshots; they can differ from the displayed pages. This report does not refresh.");
        output.AppendLine();
        output.AppendLine("Components (monitor age at generation)");
        // Deserialized JSON can violate the contract's non-null annotations.
        IEnumerable<GameHostHealthComponentResponse?>? sourceCards = snapshot?.Components;
        var cards = sourceCards?.Take(6).ToArray() ?? [];
        foreach (var (key, label) in Components)
        {
            var matches = cards.OfType<GameHostHealthComponentResponse>()
                .Where(card => string.Equals(card.Component, key, StringComparison.Ordinal)).Take(2).ToArray();
            var card = cards.Length <= 5 && matches.Length == 1 ? matches[0] : null;
            var freshness = Freshness(card?.ObservedAtUtc, generatedAt, 180);
            var state = !string.Equals(snapshotState, "Available", StringComparison.Ordinal) ? snapshotState :
                !string.Equals(freshness, "Available", StringComparison.Ordinal) ? freshness : State(card?.State);
            output.Append(label).Append(": ").Append(state).Append("; observed: ").Append(Time(card?.ObservedAtUtc))
                .Append("; age (seconds): ").AppendLine(Age(card?.ObservedAtUtc, generatedAt));
        }

        output.AppendLine();
        AppendWarnings(output, snapshot?.Warnings, generatedAt, snapshotState);
        output.AppendLine();
        output.AppendLine("Measurements (API snapshot; missing or invalid is Unknown)");
        IEnumerable<GameHostHealthValueResponse?>? sourceValues = snapshot?.Values;
        var values = sourceValues?.Take(29).ToArray() ?? [];
        foreach (var (key, label) in Metrics)
        {
            var roles = key is "diskAvailableBytes" or "diskAvailableRatio" or "inodesAvailable" or "inodesAvailableRatio"
                ? StorageRoles : [string.Empty];
            foreach (var role in roles)
            {
                var matches = values.OfType<GameHostHealthValueResponse>().Where(value => string.Equals(value.Metric, key, StringComparison.Ordinal) &&
                    string.Equals(value.Role, role, StringComparison.Ordinal)).Take(2).ToArray();
                var value = string.Equals(snapshotState, "Available", StringComparison.Ordinal) && values.Length <= 28 && matches.Length == 1 &&
                    double.IsFinite(matches[0].Value) && matches[0].Value >= 0 ? FormatValue(key, matches[0].Value) : "Unknown";
                output.Append(label);
                if (role.Length > 0)
                {
                    output.Append(" (").Append(role).Append(')');
                }

                output.Append(": ").AppendLine(value);
            }
        }

        output.AppendLine();
        output.AppendLine("Interpretation");
        output.AppendLine("Unknown and Stale never mean healthy. Cards, measurements and rule evaluations are independent snapshots.");
        output.AppendLine("CPU is a roughly one-second sample once a minute, not a minute average, game latency or FPS; no CPU utilization warning threshold.");
        output.AppendLine("Backup copy age/source and weekly outcome are separate. A newer manual copy does not close an uncertain weekly slot.");
        output.AppendLine("A local heartbeat does not prove IRM delivery. Missing inode data is not zero free inodes; zero swap use is valid.");
        output.AppendLine("The game-host section covers the existing 17 warning rules, not all monitoring or an availability guarantee.");
        output.AppendLine("Guidance is read-only; recovery actions require a separately reviewed plan.");
        output.Append("Guide: ").AppendLine(GameHostWarningGuidance.RunbookHome);
        AppendArchive(output, archive, generatedAt);
        return output.ToString();
    }

    private static void AppendArchive(StringBuilder output, ArchiveHealthResponse? source, DateTimeOffset generatedAt)
    {
        var archive = ArchiveHealthView.Create(source, generatedAt);
        output.AppendLine();
        output.AppendLine("Archive health (independent source)");
        output.Append("Archive snapshot evaluated: ").AppendLine(Time(source?.EvaluatedAtUtc));
        output.Append("Archive snapshot age (seconds): ").AppendLine(Age(source?.EvaluatedAtUtc, generatedAt));
        output.Append("Archive observation: ").AppendLine(archive.ObservationState);
        output.Append("Archive observer last checked: ").AppendLine(Time(archive.ObservedAtUtc));
        output.Append("Archive observer age (seconds): ").AppendLine(Age(archive.ObservedAtUtc, generatedAt));
        output.Append("Accepted scheduled success found: ").AppendLine(archive.HasScheduledSuccess switch { true => "Yes", false => "No", _ => "Unknown" });
        output.Append("Last successful scheduled run: ").AppendLine(Time(archive.LastSuccessAtUtc));
        output.Append("Last successful scheduled run age (seconds): ").AppendLine(
            archive.LastSuccessAgeSeconds?.ToString("0.###", CultureInfo.InvariantCulture) ?? "Unknown");
        output.Append("Archive warning snapshot: ").Append(archive.WarningsState).Append("; observed: ").Append(Time(archive.WarningsObservedAtUtc))
            .Append("; age (seconds): ").AppendLine(Age(archive.WarningsObservedAtUtc, generatedAt));
        foreach (var definition in ArchiveHealthView.Definitions)
        {
            var state = archive.Warnings.SingleOrDefault(item => string.Equals(item.Name, definition.Name, StringComparison.Ordinal))?.State ?? "Unknown";
            output.Append(definition.Name).Append(": ").AppendLine(state);
            output.Append("First check: ").AppendLine(definition.FirstCheck);
        }

        output.AppendLine("Unknown and Stale do not establish Archive freshness or absence of warnings.");
        output.AppendLine("The observer timestamp must be no older than 30 minutes; rule evaluations must be no older than 60 seconds.");
        output.AppendLine("Age starts at creation of the last accepted first-attempt scheduled success on main. Manual runs and reruns do not refresh it.");
        output.AppendLine("An Available observation can coexist with a Firing delay warning. A successful workflow does not verify its stored object.");
        output.AppendLine("Recovery, source completeness, notification delivery and an achieved SLO are separate evidence.");
        output.AppendLine("Preserve history before a separately reviewed recovery; do not automatically dispatch or retry.");
        output.Append("Guide: ").AppendLine(ArchiveHealthView.Runbook);
    }

    private static void AppendWarnings(StringBuilder output, GameHostWarningsResponse? warnings,
        DateTimeOffset generatedAt, string snapshotState)
    {
        output.AppendLine("Active warnings");
        IEnumerable<GameHostWarningResponse?>? sourceItems = warnings?.Items;
        var items = sourceItems?.Take(18).ToArray();
        var state = string.Equals(snapshotState, "Available", StringComparison.Ordinal) ? warnings?.State : snapshotState;
        var freshness = Freshness(warnings?.ObservedAtUtc, generatedAt, 60);
        if (string.Equals(state, "Available", StringComparison.Ordinal))
        {
            state = freshness;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (items is null || items.Length > 17 || items.Any(item => item is null ||
                string.IsNullOrEmpty(item.Name) || GameHostWarningGuidance.ForWarning(item.Name) is null ||
                item.State is not ("Pending" or "Firing") || !seen.Add(item.Name)))
            {
                state = "Unknown";
            }
        }

        state = state is "Available" or "Stale" ? state : "Unknown";
        output.Append("Warning snapshot: ").Append(state).Append("; observed: ").Append(Time(warnings?.ObservedAtUtc))
            .Append("; age (seconds): ").AppendLine(Age(warnings?.ObservedAtUtc, generatedAt));
        if (!string.Equals(state, "Available", StringComparison.Ordinal))
        {
            output.AppendLine("Warning data is unavailable, incomplete or stale; absence of warnings is not established.");
            AppendGuidance(output, string.Equals(state, "Stale", StringComparison.Ordinal) ? GameHostWarningGuidance.Stale : GameHostWarningGuidance.Unknown);
            return;
        }

        if (items!.Length == 0)
        {
            output.AppendLine("No pending or firing warnings in this fresh rule snapshot.");
        }

        foreach (var item in items.OfType<GameHostWarningResponse>().OrderBy(static item => string.Equals(item.State, "Firing", StringComparison.Ordinal) ? 0 : 1).ThenBy(static item => item.Name, StringComparer.Ordinal))
        {
            output.Append(item.Name).Append(": ").AppendLine(item.State);
            AppendGuidance(output, GameHostWarningGuidance.ForWarning(item.Name)!);
        }
    }

    private static void AppendGuidance(StringBuilder output, GameHostWarningGuidance guidance)
    {
        output.Append("First check: ").AppendLine(guidance.FirstCheck);
        output.Append("Preserve: ").AppendLine(guidance.Preserve);
        output.Append("Guide: ").AppendLine(guidance.RunbookUrl);
    }

    private static string Freshness(DateTimeOffset? value, DateTimeOffset now, int seconds) =>
        value is null || value > now ? "Unknown" : now - value > TimeSpan.FromSeconds(seconds) ? "Stale" : "Available";

    private static string Time(DateTimeOffset? value) => value?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) ?? "Unknown";

    private static string Age(DateTimeOffset? value, DateTimeOffset now) => value is null || value > now
        ? "Unknown" : (now - value.Value).TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static string State(string? state) => state switch
    {
        "NoWarnings" => "No warnings",
        "Observed" => "Observed (no CPU threshold)",
        "Pending" => "Pending",
        "Firing" => "Firing",
        "Stale" => "Stale",
        _ => "Unknown"
    };

    private static string FormatValue(string metric, double value) => metric switch
    {
        "backupSource" => value switch { 0 => "No copy", 1 => "Weekly", 2 => "Manual", _ => "Unknown" },
        "weeklyAttempt" => value switch
        {
            0 => "No attempt",
            1 => "Copy verified",
            2 => "Empty data",
            3 => "Pending",
            4 => "Skipped",
            5 => "Verification failed",
            6 => "Capture failed",
            7 => "Interrupted",
            8 => "Closed: capture did not start",
            9 => "Closed: outcome uncertain",
            _ => "Unknown"
        },
        "weeklyMissed" => value switch { 0 => "No", 1 => "Yes", _ => "Unknown" },
        _ => value.ToString("0.###", CultureInfo.InvariantCulture)
    };
}
