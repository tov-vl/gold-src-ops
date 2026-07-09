namespace GoldSrcOps.Domain.Servers;

public sealed class Server
{
    private Server()
    {
        Name = string.Empty;
        Endpoint = null!;
    }

    public Server(
        string name,
        GameServerKind game,
        ServerEndpoint endpoint,
        int pollIntervalSeconds,
        string? notes,
        DateTimeOffset createdAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(endpoint);

        if (pollIntervalSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pollIntervalSeconds), "Poll interval must be positive.");
        }

        Id = Guid.NewGuid();
        Name = name.Trim();
        Game = game;
        Endpoint = endpoint;
        IsEnabled = true;
        PollIntervalSeconds = pollIntervalSeconds;
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        CreatedAtUtc = createdAtUtc;
        CurrentState = ServerCurrentState.CreateUnknown(Id, createdAtUtc);
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; }

    public GameServerKind Game { get; private set; }

    public ServerEndpoint Endpoint { get; private set; }

    public bool IsEnabled { get; private set; }

    public int PollIntervalSeconds { get; private set; }

    public string? Notes { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public ServerCurrentState? CurrentState { get; private set; }

    public void UpdateDetails(
        string name,
        ServerEndpoint endpoint,
        int pollIntervalSeconds,
        string? notes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(endpoint);

        if (pollIntervalSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pollIntervalSeconds), "Poll interval must be positive.");
        }

        Name = name.Trim();
        Endpoint = endpoint;
        PollIntervalSeconds = pollIntervalSeconds;
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
    }

    public bool IsDueForPolling(DateTimeOffset nowUtc)
    {
        if (!IsEnabled)
        {
            return false;
        }

        if (CurrentState is null || CurrentState.Status == ServerStatus.Unknown)
        {
            return true;
        }

        return CurrentState.LastCheckedAtUtc.AddSeconds(PollIntervalSeconds) <= nowUtc;
    }

    public ServerCurrentState GetCurrentState(DateTimeOffset nowUtc)
    {
        CurrentState ??= ServerCurrentState.CreateUnknown(Id, nowUtc);
        return CurrentState;
    }
}
