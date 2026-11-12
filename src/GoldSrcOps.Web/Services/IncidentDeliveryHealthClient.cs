using GoldSrcOps.Contracts.Alerts;
using GoldSrcOps.Contracts.ProviderDelivery;

namespace GoldSrcOps.Web.Services;

internal sealed class IncidentDeliveryHealthClient(IReaderApiClient reader, IProviderOperationsClient provider) : IIncidentDeliveryHealthClient
{
    public bool IsReceiverConfigured => provider.IsEnabled;
    public Task<AlertDeliveryStatusResponse> ReadOutboxAsync(CancellationToken cancellationToken) => ReadWithinBudgetAsync(reader.GetAlertDeliveryStatusAsync, cancellationToken);
    public Task<ProviderDeliveryStatusResponse> ReadReceiverAsync(CancellationToken cancellationToken) => ReadWithinBudgetAsync(provider.GetStatusAsync, cancellationToken);

    private static async Task<T> ReadWithinBudgetAsync<T>(Func<CancellationToken, Task<T>> read, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(10));
        return await read(budget.Token).ConfigureAwait(false);
    }
}
