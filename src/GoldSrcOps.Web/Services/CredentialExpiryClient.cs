using GoldSrcOps.Contracts.Monitoring;

namespace GoldSrcOps.Web.Services;

internal sealed class CredentialExpiryClient(HttpClient client) : ICredentialExpiryClient
{
    public async Task<CredentialExpiryResponse> ReadAsync(CancellationToken cancellationToken = default) =>
        await client.GetFromJsonAsync<CredentialExpiryResponse>("api/operator/credential-expiry", cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidDataException("Credential expiry metadata is unavailable.");
}
