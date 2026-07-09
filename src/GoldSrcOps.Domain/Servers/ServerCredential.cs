namespace GoldSrcOps.Domain.Servers;

public sealed class ServerCredential
{
    public const int MaxSecretReferenceLength = 512;

    private ServerCredential()
    {
        SecretReference = string.Empty;
    }

    public ServerCredential(
        Guid serverId,
        ServerCredentialKind kind,
        string secretReference,
        DateTimeOffset createdAtUtc)
    {
        if (serverId == Guid.Empty)
        {
            throw new ArgumentException("Server id must not be empty.", nameof(serverId));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), "Credential kind is not supported.");
        }

        Id = Guid.NewGuid();
        ServerId = serverId;
        Kind = kind;
        SecretReference = NormalizeSecretReference(secretReference);
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid ServerId { get; private set; }

    public ServerCredentialKind Kind { get; private set; }

    public string SecretReference { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? UpdatedAtUtc { get; private set; }

    public Server Server { get; private set; } = null!;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(SecretReference);

    public void UpdateSecretReference(string secretReference, DateTimeOffset updatedAtUtc)
    {
        SecretReference = NormalizeSecretReference(secretReference);
        UpdatedAtUtc = updatedAtUtc;
    }

    private static string NormalizeSecretReference(string secretReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretReference);

        var normalized = secretReference.Trim();
        if (normalized.Length > MaxSecretReferenceLength)
        {
            throw new ArgumentException(
                $"Secret reference must not exceed {MaxSecretReferenceLength} characters.",
                nameof(secretReference));
        }

        return normalized;
    }
}
