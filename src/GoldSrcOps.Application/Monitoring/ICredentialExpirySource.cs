namespace GoldSrcOps.Application.Monitoring;

public interface ICredentialExpirySource
{
    IReadOnlyList<CredentialExpiryMetadata> Read();
}
