namespace GoldSrcOps.Infrastructure.Commands;

internal enum SecretReferenceResolutionResultKind
{
    Resolved = 1,
    NotFound = 2,
    Unsupported = 3
}

internal sealed record SecretReferenceResolutionResult(
    SecretReferenceResolutionResultKind Kind,
    string? Secret)
{
    public static SecretReferenceResolutionResult Resolved(string secret) =>
        new(SecretReferenceResolutionResultKind.Resolved, secret);

    public static SecretReferenceResolutionResult NotFound() =>
        new(SecretReferenceResolutionResultKind.NotFound, Secret: null);

    public static SecretReferenceResolutionResult Unsupported() =>
        new(SecretReferenceResolutionResultKind.Unsupported, Secret: null);
}
