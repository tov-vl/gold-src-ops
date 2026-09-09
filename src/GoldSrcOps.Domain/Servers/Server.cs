namespace GoldSrcOps.Domain.Servers;

public sealed class Server
{
    public const int MaxNameLength = 200;
    public const int MaxNotesLength = 2000;
    public const int RegistrationIntentHashLength = 64;

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
        DateTimeOffset createdAtUtc,
        bool isEnabled = true,
        Guid? registrationRequestId = null,
        string? registrationIntentHash = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(endpoint);

        var normalizedName = name.Trim();
        if (normalizedName.Length > MaxNameLength)
        {
            throw new ArgumentException(
                $"Server name must not exceed {MaxNameLength} characters.",
                nameof(name));
        }

        if (pollIntervalSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pollIntervalSeconds), "Poll interval must be positive.");
        }

        var normalizedNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        if (normalizedNotes?.Length > MaxNotesLength)
        {
            throw new ArgumentException(
                $"Notes must not exceed {MaxNotesLength} characters.",
                nameof(notes));
        }

        ValidateRegistrationIntent(registrationRequestId, registrationIntentHash);

        Id = Guid.NewGuid();
        Name = normalizedName;
        Game = game;
        Endpoint = endpoint;
        IsEnabled = isEnabled;
        PollIntervalSeconds = pollIntervalSeconds;
        Notes = normalizedNotes;
        CreatedAtUtc = createdAtUtc;
        RegistrationRequestId = registrationRequestId;
        RegistrationIntentHash = registrationIntentHash;
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

    public Guid? RegistrationRequestId { get; private set; }

    public string? RegistrationIntentHash { get; private set; }

    public ServerCurrentState? CurrentState { get; private set; }

    public void Enable()
    {
        IsEnabled = true;
    }

    public void Disable()
    {
        IsEnabled = false;
    }

    public void UpdateDetails(
        string name,
        ServerEndpoint endpoint,
        int pollIntervalSeconds,
        string? notes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(endpoint);

        var normalizedName = name.Trim();
        if (normalizedName.Length > MaxNameLength)
        {
            throw new ArgumentException(
                $"Server name must not exceed {MaxNameLength} characters.",
                nameof(name));
        }

        if (pollIntervalSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pollIntervalSeconds), "Poll interval must be positive.");
        }

        var normalizedNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        if (normalizedNotes?.Length > MaxNotesLength)
        {
            throw new ArgumentException(
                $"Notes must not exceed {MaxNotesLength} characters.",
                nameof(notes));
        }

        Name = normalizedName;
        Endpoint = endpoint;
        PollIntervalSeconds = pollIntervalSeconds;
        Notes = normalizedNotes;
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

    private static void ValidateRegistrationIntent(
        Guid? registrationRequestId,
        string? registrationIntentHash)
    {
        if (registrationRequestId is null && registrationIntentHash is null)
        {
            return;
        }

        if (registrationRequestId is null ||
            registrationRequestId == Guid.Empty ||
            registrationIntentHash is null ||
            registrationIntentHash.Length != RegistrationIntentHashLength ||
            registrationIntentHash.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "Registration request ID and intent hash must form a valid idempotency pair.",
                nameof(registrationRequestId));
        }
    }
}
