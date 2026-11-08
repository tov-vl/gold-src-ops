using GoldSrcOps.Contracts.Monitoring;

namespace GoldSrcOps.Web.Services;

internal interface ICredentialExpiryClient
{
    Task<CredentialExpiryResponse> ReadAsync(CancellationToken cancellationToken = default);
}
