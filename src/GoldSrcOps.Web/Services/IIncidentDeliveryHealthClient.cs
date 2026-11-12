using GoldSrcOps.Contracts.Alerts;
using GoldSrcOps.Contracts.ProviderDelivery;

namespace GoldSrcOps.Web.Services;

internal interface IIncidentDeliveryHealthClient
{
    bool IsReceiverConfigured { get; }
    Task<AlertDeliveryStatusResponse> ReadOutboxAsync(CancellationToken cancellationToken);
    Task<ProviderDeliveryStatusResponse> ReadReceiverAsync(CancellationToken cancellationToken);
}
