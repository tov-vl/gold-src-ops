namespace GoldSrcOps.Contracts.Credentials;

public sealed record SetRconCredentialRequest(
    long ExpectedServerRevision,
    long ExpectedCredentialRevision,
    string SecretAlias)
{
    public const int MaxSecretAliasLength = 128;
}
