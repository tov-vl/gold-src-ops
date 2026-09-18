using GoldSrcOps.AlertReceiver.Configuration;
using GoldSrcOps.AlertReceiver.Telemetry;
using Microsoft.Extensions.Options;

namespace GoldSrcOps.AlertReceiver.ProviderDelivery;

internal enum ProviderDispatchResult
{
    NoMessage,
    Delivered,
    RetryScheduled,
    DeadLettered,
    ClaimLost,
}

internal sealed class ProviderDispatcher(
    IProviderOutboxStore outbox,
    IProviderDeliveryChannel deliveryChannel,
    IProviderRetryDelayProvider retryDelayProvider,
    TimeProvider timeProvider,
    IOptions<ProviderDeliveryOptions> options)
{
    private readonly ProviderDeliveryOptions _options = options.Value;

    public async Task<ProviderDispatchResult> DispatchNextAsync(CancellationToken cancellationToken)
    {
        var message = await outbox.ClaimNextAsync(timeProvider.GetUtcNow(), cancellationToken);
        if (message is null)
        {
            return ProviderDispatchResult.NoMessage;
        }

        ProviderDeliveryResult deliveryResult;
        try
        {
            deliveryResult = await deliveryChannel.DeliverAsync(message, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ReceiverMetrics.RecordAttempt("interrupted");
            throw;
        }
        catch
        {
            deliveryResult = ProviderDeliveryResult.Retryable("Provider delivery failed unexpectedly.");
        }

        var completedAtUtc = timeProvider.GetUtcNow();
        if (deliveryResult.Kind == ProviderDeliveryResultKind.Delivered)
        {
            return Complete(
                await outbox.MarkProcessedAsync(
                    message.Id,
                    message.ClaimId,
                    completedAtUtc,
                    cancellationToken),
                ProviderDispatchResult.Delivered,
                "delivered");
        }

        if (deliveryResult.Kind == ProviderDeliveryResultKind.RetryableFailure &&
            message.AttemptCount < _options.MaxAttempts)
        {
            var delay = deliveryResult.RetryAfter is { } retryAfter &&
                retryAfter >= TimeSpan.Zero &&
                retryAfter <= _options.MaximumRetryDelay
                    ? retryAfter
                    : retryDelayProvider.GetDelay(message.AttemptCount);
            return Complete(
                await outbox.ScheduleRetryAsync(
                    message.Id,
                    message.ClaimId,
                    completedAtUtc + delay,
                    deliveryResult.FailureSummary,
                    cancellationToken),
                ProviderDispatchResult.RetryScheduled,
                "retry_scheduled");
        }

        return Complete(
            await outbox.MarkDeadLetterAsync(
                message.Id,
                message.ClaimId,
                completedAtUtc,
                deliveryResult.FailureSummary,
                cancellationToken),
            ProviderDispatchResult.DeadLettered,
            "dead_lettered");
    }

    public async Task<ProviderClaimRecoveryResult> RecoverExpiredClaimsAsync(
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var recovered = await outbox.RecoverExpiredClaimsAsync(
            now - _options.ClaimTimeout,
            now,
            _options.MaxAttempts,
            cancellationToken);
        ReceiverMetrics.RecordRecovery(recovered.Total);
        return recovered;
    }

    public async Task RefreshStatisticsAsync(CancellationToken cancellationToken)
    {
        var statistics = await outbox.GetStatisticsAsync(cancellationToken);
        ReceiverMetrics.UpdateStatistics(statistics, timeProvider.GetUtcNow());
    }

    public async Task<int> CleanupProcessedAsync(CancellationToken cancellationToken)
    {
        var deleted = await outbox.DeleteProcessedBatchAsync(
            timeProvider.GetUtcNow() - _options.ProcessedRetentionPeriod,
            _options.CleanupBatchSize,
            cancellationToken);
        ReceiverMetrics.RecordProcessedDeleted(deleted);
        return deleted;
    }

    private static ProviderDispatchResult Complete(
        bool claimUpdated,
        ProviderDispatchResult success,
        string metricResult)
    {
        var result = claimUpdated ? success : ProviderDispatchResult.ClaimLost;
        ReceiverMetrics.RecordAttempt(claimUpdated ? metricResult : "claim_lost");
        return result;
    }
}
