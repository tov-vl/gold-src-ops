namespace GoldSrcOps.Infrastructure.Commands;

internal interface ISecretReferenceResolver
{
    Task<SecretReferenceResolutionResult> ResolveAsync(
        string secretReference,
        CancellationToken cancellationToken);
}
