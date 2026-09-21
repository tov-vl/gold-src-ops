namespace GoldSrcOps.AlertReceiver.Configuration;

internal sealed class ProviderOperationsOptions
{
    public const string SectionName = "ProviderOperations";
    public const int MaxAuthorizationLength = 8192;

    public bool Enabled { get; init; }

    public string Authorization { get; init; } = string.Empty;

    public bool IsValid() =>
        !Enabled ||
        (!string.IsNullOrWhiteSpace(Authorization) &&
            Authorization.Length <= MaxAuthorizationLength);
}
