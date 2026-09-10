namespace GoldSrcOps.Application.Credentials;

public enum SetServerCredentialResultKind
{
    Updated,
    NotFound,
    MonitoringEnabled,
    CommandsInProgress,
    RevisionConflict
}

public sealed record SetServerCredentialResult(
    SetServerCredentialResultKind Kind,
    ServerCredentialDto? Credential)
{
    public static SetServerCredentialResult Updated(ServerCredentialDto credential) =>
        new(SetServerCredentialResultKind.Updated, credential);

    public static SetServerCredentialResult NotFound() =>
        new(SetServerCredentialResultKind.NotFound, Credential: null);

    public static SetServerCredentialResult MonitoringEnabled() =>
        new(SetServerCredentialResultKind.MonitoringEnabled, Credential: null);

    public static SetServerCredentialResult CommandsInProgress() =>
        new(SetServerCredentialResultKind.CommandsInProgress, Credential: null);

    public static SetServerCredentialResult RevisionConflict() =>
        new(SetServerCredentialResultKind.RevisionConflict, Credential: null);
}
