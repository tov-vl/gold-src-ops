using System.Globalization;
using System.Text.Json;
using GoldSrcOps.Contracts.Monitoring;

namespace GoldSrcOps.Web.Services;

internal sealed record OperatorHealthOverview(DateTimeOffset GeneratedAtUtc, IReadOnlyList<OperatorHealthOverview.Source> Sources)
{
    public const string Path = "/operator/health";

    private static readonly (string Key, string Label)[] Components =
    [
        ("agent", "Agent / queue"), ("backup", "Player backup"), ("storage", "Disk / inode"),
        ("memory", "RAM / swap"), ("cpu", "Sampled CPU")
    ];

    public static async Task<OperatorHealthOverview> ReadAsync(IGameHostHealthClient gameHost, IArchiveHealthClient archive,
        IPollingHealthClient polling, ICredentialExpiryClient credentials, TimeProvider clock, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var gameRead = ReadSourceAsync(gameHost.ReadAsync, cancellationToken);
        var archiveRead = ReadSourceAsync(archive.ReadAsync, cancellationToken);
        var pollingRead = ReadSourceAsync(polling.ReadAsync, cancellationToken);
        var credentialRead = ReadSourceAsync(credentials.ReadAsync, cancellationToken);
        await Task.WhenAll(gameRead, archiveRead, pollingRead, credentialRead).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return Create(await gameRead.ConfigureAwait(false), await archiveRead.ConfigureAwait(false),
            await pollingRead.ConfigureAwait(false), await credentialRead.ConfigureAwait(false), clock.GetUtcNow());
    }

    public static OperatorHealthOverview Create(GameHostHealthResponse? gameHost, ArchiveHealthResponse? archive,
        PollingHealthResponse? polling, CredentialExpiryResponse? credentials, DateTimeOffset now) =>
        new(now, [GameHost(gameHost, now), Archive(archive, now), Polling(polling, now), Credentials(credentials, now)]);

    private static async Task<T?> ReadSourceAsync<T>(Func<CancellationToken, Task<T>> read, CancellationToken cancellationToken) where T : class
    {
        try
        {
            return await read(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested &&
            exception is HttpRequestException or TaskCanceledException or InvalidDataException or JsonException)
        {
            return null;
        }
    }

    private static Source GameHost(GameHostHealthResponse? source, DateTimeOffset now)
    {
        var state = Freshness(source?.EvaluatedAtUtc, now, 180);
        IEnumerable<GameHostHealthComponentResponse?>? input = source?.Components;
        var components = input?.Take(6).ToArray();
        var facts = Components.Select(definition =>
        {
            var matches = components?.Where(item => string.Equals(item?.Component, definition.Key, StringComparison.Ordinal)).Take(2).ToArray();
            var item = components is { Length: <= 5 } && matches is { Length: 1 } ? matches[0] : null;
            var observed = ValidTime(item?.ObservedAtUtc, source?.EvaluatedAtUtc);
            var observedState = Freshness(observed, now, 180);
            var value = state is not "Available" ? state : observedState is not "Available" ? observedState : item?.State switch
            {
                "NoWarnings" => "No warnings",
                "Observed" => "Observed (no CPU threshold)",
                "Pending" => "Pending",
                "Firing" => "Firing",
                "Stale" => "Stale",
                _ => "Unknown"
            };
            return new Fact(definition.Label, value + "; observed: " + Time(observed));
        }).ToArray();

        var warnings = source?.Warnings;
        var rulesTime = ValidTime(warnings?.ObservedAtUtc, source?.EvaluatedAtUtc);
        var rulesState = state is "Available" ? Freshness(rulesTime, now, 60) : state;
        IEnumerable<GameHostWarningResponse?>? warningInput = warnings?.Items;
        var items = warningInput?.Take(18).ToArray();
        if (warnings?.State is not "Available")
        {
            rulesState = warnings?.State is "Stale" && rulesTime is not null && state is "Available" ? "Stale" : "Unknown";
        }

        if (rulesState is "Available" && (items is null || items.Length > 17 ||
            items.Any(item => item is null || string.IsNullOrEmpty(item.Name) || GameHostWarningGuidance.ForWarning(item.Name) is null || item.State is not ("Pending" or "Firing")) ||
            items.OfType<GameHostWarningResponse>().Select(static item => item.Name).Distinct(StringComparer.Ordinal).Count() != items.Length))
        {
            rulesState = "Unknown";
        }

        var active = rulesState is "Available" ? items!.OfType<GameHostWarningResponse>().Select(static item => new Warning(item.Name, item.State)) : [];
        return new Source("game-host", "Game-host health", "/operator/game-host-health", state, ValidTime(source?.EvaluatedAtUtc, now), facts,
            CreateWarnings(rulesState, rulesTime, active), "Five host observations and the existing 17 warning rules. Sampled CPU is not game latency or FPS.");
    }

    private static Source Archive(ArchiveHealthResponse? source, DateTimeOffset now)
    {
        var view = ArchiveHealthView.Create(source, now);
        return new Source("archive", "Archive health", ArchiveHealthView.Path, Freshness(source?.EvaluatedAtUtc, now, 60), ValidTime(source?.EvaluatedAtUtc, now),
        [
            new Fact("Observation", view.ObservationState),
            new Fact("Observer last checked", Time(view.ObservedAtUtc)),
            new Fact("Accepted scheduled success", view.HasScheduledSuccess switch { true => "Yes", false => "No", _ => "Unknown" }),
            new Fact("Last scheduled success", Time(view.LastSuccessAtUtc)),
            new Fact("Success age (seconds)", Seconds(view.LastSuccessAgeSeconds))
        ], CreateWarnings(view.WarningsState, view.WarningsObservedAtUtc, view.Warnings.Select(static item => new Warning(item.Name, item.State))),
            "Schedule age and observer freshness are separate. A successful workflow does not verify its stored object; Recovery is separate.");
    }

    private static Source Polling(PollingHealthResponse? source, DateTimeOffset now)
    {
        var view = PollingHealthView.Create(source, now);
        return new Source("polling", "Polling health", PollingHealthView.Path, Freshness(source?.EvaluatedAtUtc, now, 60), ValidTime(source?.EvaluatedAtUtc, now),
        [
            new Fact("Worker state", view.ObservationState),
            new Fact("Worker last observed", Time(view.ObservedAtUtc)),
            new Fact("Last cycle completed", Time(view.LastCycleFinishedAtUtc)),
            new Fact("Completion age (seconds)", Seconds(view.CompletionAgeSeconds)),
            new Fact("Observation budget (seconds)", Seconds(view.CycleBudgetSeconds))
        ], CreateWarnings(view.WarningsState, view.WarningsObservedAtUtc, view.Warnings.Select(static item => new Warning(item.Name, item.State))),
            "Completed empty passes and failed A2S replies are worker progress. The budget is not a query deadline or latency SLO.");
    }

    private static Source Credentials(CredentialExpiryResponse? source, DateTimeOffset now)
    {
        var entries = CredentialExpiryView.Create(source, now);
        return new Source("credentials", "Credential expiry", CredentialExpiryView.Path, Freshness(source?.EvaluatedAtUtc, now, 180), ValidTime(source?.EvaluatedAtUtc, now),
            [.. CredentialExpiryView.Catalog.Select((label, index) => new Fact(label.Purpose,
                CredentialExpiryView.StateLabel(entries[index].State, entries[index].ExpiresAtMinuteUtc is not null), entries[index].State))], Rules: null,
            "Recorded dates, not live provider checks or proof that a credential works. Details retain expiry precision and the actual last verification time.");
    }

    private static WarningSnapshot CreateWarnings(string state, DateTimeOffset? observed, IEnumerable<Warning> items) =>
        new(state, observed, state is "Available" ? [.. items.Where(static item => item.State is "Pending" or "Firing")
            .OrderBy(static item => item.State is "Firing" ? 0 : 1).ThenBy(static item => item.Name, StringComparer.Ordinal)] : []);

    private static string Freshness(DateTimeOffset? value, DateTimeOffset now, int seconds) =>
        value is null || value < DateTimeOffset.UnixEpoch || value > now ? "Unknown" : now - value > TimeSpan.FromSeconds(seconds) ? "Stale" : "Available";

    private static DateTimeOffset? ValidTime(DateTimeOffset? value, DateTimeOffset? latest) =>
        value >= DateTimeOffset.UnixEpoch && value <= latest ? value : null;

    private static string Time(DateTimeOffset? value) => value?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) ?? "Unknown";
    private static string Seconds(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "Unknown";

    internal sealed record Source(string Key, string Title, string DetailsPath, string SnapshotState, DateTimeOffset? EvaluatedAtUtc,
        IReadOnlyList<Fact> Facts, WarningSnapshot? Rules, string Note);
    internal sealed record Fact(string Label, string Value, string? State = null);
    internal sealed record Warning(string Name, string State);
    internal sealed record WarningSnapshot(string State, DateTimeOffset? ObservedAtUtc, IReadOnlyList<Warning> Items);
}
