using System.Diagnostics;
using System.Text.Json;
using GoldSrcOps.Application.Common;
using GoldSrcOps.Application.Telemetry;
using Microsoft.Extensions.Logging;

namespace GoldSrcOps.Application.Alerts;

public sealed partial class AlertDispatcher
{
    public const string ExpiredClaimFailureReason =
        "Delivery claim expired before completion.";

    public const string ExhaustedClaimFailureReason =
        "Delivery claim expired after the maximum number of attempts.";

    private readonly IOutboxStore _outbox;
    private readonly IAlertDeliveryChannel _deliveryChannel;
    private readonly IAlertRetryDelayProvider _retryDelayProvider;
    private readonly IClock _clock;
    private readonly AlertDispatcherSettings _settings;
    private readonly ILogger<AlertDispatcher> _logger;

    public AlertDispatcher(
        IOutboxStore outbox,
        IAlertDeliveryChannel deliveryChannel,
        IAlertRetryDelayProvider retryDelayProvider,
        IClock clock,
        AlertDispatcherSettings settings,
        ILogger<AlertDispatcher> logger)
    {
        _outbox = outbox;
        _deliveryChannel = deliveryChannel;
        _retryDelayProvider = retryDelayProvider;
        _clock = clock;
        _settings = settings;
        _logger = logger;
    }

    public async Task<AlertDispatchAttemptResult> DispatchNextAsync(
        CancellationToken cancellationToken)
    {
        var message = await _outbox.ClaimNextPendingAsync(_clock.UtcNow, cancellationToken);
        if (message is null)
        {
            return AlertDispatchAttemptResult.NoMessage();
        }

        var serverId = TryReadServerId(message);
        var startedTimestamp = Stopwatch.GetTimestamp();
        LogDeliveryAttemptStarted(
            _logger,
            message.Id,
            message.EventType,
            serverId,
            message.AggregateId,
            message.AttemptCount,
            message.ClaimId);

        try
        {
            AlertDeliveryAttemptResult deliveryResult;
            try
            {
                deliveryResult = await _deliveryChannel.DeliverAsync(message, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                deliveryResult = AlertDeliveryAttemptResult.RetryableFailure(
                    AlertDeliveryFailureCategory.Unexpected);
            }

            return await ApplyDeliveryResultAsync(
                message,
                serverId,
                deliveryResult,
                startedTimestamp,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var duration = Stopwatch.GetElapsedTime(startedTimestamp);
            GoldSrcOpsMetrics.RecordAlertDeliveryAttempt(
                AlertDeliveryMetricResult.Interrupted,
                duration);
            LogDeliveryAttemptInterrupted(
                _logger,
                message.Id,
                message.EventType,
                serverId,
                message.AggregateId,
                message.AttemptCount,
                message.ClaimId,
                duration.TotalMilliseconds);
            throw;
        }
        catch (Exception exception)
        {
            var duration = Stopwatch.GetElapsedTime(startedTimestamp);
            GoldSrcOpsMetrics.RecordAlertDeliveryAttempt(AlertDeliveryMetricResult.Faulted, duration);
            LogDeliveryAttemptFaulted(
                _logger,
                message.Id,
                message.EventType,
                serverId,
                message.AggregateId,
                message.AttemptCount,
                message.ClaimId,
                exception.GetType().Name,
                duration.TotalMilliseconds);
            throw;
        }
    }

    public async Task<OutboxClaimRecoveryResult> RecoverExpiredClaimsAsync(
        CancellationToken cancellationToken)
    {
        var recoveredAtUtc = _clock.UtcNow;
        var recovered = await _outbox.RecoverExpiredClaimsAsync(
            recoveredAtUtc - _settings.ClaimTimeout,
            recoveredAtUtc,
            _settings.MaxAttempts,
            ExpiredClaimFailureReason,
            ExhaustedClaimFailureReason,
            cancellationToken);

        GoldSrcOpsMetrics.RecordAlertClaimsRecovered(recovered.TotalRecovered);
        GoldSrcOpsMetrics.RecordAlertDeadLetters(recovered.DeadLettered);
        return recovered;
    }

    public async Task<OutboxCleanupResult> CleanupProcessedAsync(
        CancellationToken cancellationToken)
    {
        var cutoffUtc = _clock.UtcNow - _settings.ProcessedRetentionPeriod;
        var deleted = await _outbox.DeleteProcessedBatchOlderThanAsync(
            cutoffUtc,
            _settings.CleanupBatchSize,
            cancellationToken);

        GoldSrcOpsMetrics.RecordAlertProcessedMessagesDeleted(deleted);
        return new OutboxCleanupResult(
            cutoffUtc,
            deleted,
            BatchLimitReached: deleted == _settings.CleanupBatchSize);
    }

    public async Task<OutboxStatistics> RefreshStatisticsAsync(
        CancellationToken cancellationToken)
    {
        var statistics = await _outbox.GetStatisticsAsync(cancellationToken);
        var oldestPendingAge = statistics.OldestPendingAtUtc is { } oldestPendingAtUtc
            ? _clock.UtcNow - oldestPendingAtUtc
            : (TimeSpan?)null;

        GoldSrcOpsMetrics.UpdateAlertOutboxStatistics(
            statistics.PendingCount,
            oldestPendingAge,
            statistics.DeadLetterCount);

        return statistics;
    }

    private async Task<AlertDispatchAttemptResult> ApplyDeliveryResultAsync(
        ClaimedOutboxMessage message,
        Guid? serverId,
        AlertDeliveryAttemptResult deliveryResult,
        long startedTimestamp,
        CancellationToken cancellationToken)
    {
        var completedAtUtc = _clock.UtcNow;

        return deliveryResult.Kind switch
        {
            AlertDeliveryAttemptResultKind.Delivered => await MarkDeliveredAsync(
                message,
                serverId,
                completedAtUtc,
                deliveryResult,
                startedTimestamp,
                cancellationToken),
            AlertDeliveryAttemptResultKind.RetryableFailure
                when message.AttemptCount < _settings.MaxAttempts => await ScheduleRetryAsync(
                    message,
                    serverId,
                    completedAtUtc,
                    deliveryResult,
                    startedTimestamp,
                    cancellationToken),
            AlertDeliveryAttemptResultKind.RetryableFailure or
                AlertDeliveryAttemptResultKind.PermanentFailure => await MarkDeadLetterAsync(
                    message,
                    serverId,
                    deliveryResult,
                    startedTimestamp,
                    cancellationToken),
            _ => throw new InvalidOperationException(
                $"Unsupported alert delivery result '{deliveryResult.Kind}'.")
        };
    }

    private async Task<AlertDispatchAttemptResult> MarkDeliveredAsync(
        ClaimedOutboxMessage message,
        Guid? serverId,
        DateTimeOffset completedAtUtc,
        AlertDeliveryAttemptResult deliveryResult,
        long startedTimestamp,
        CancellationToken cancellationToken)
    {
        var updated = await _outbox.MarkProcessedAsync(
            message.Id,
            message.ClaimId,
            completedAtUtc,
            cancellationToken);

        return CompleteAttempt(
            message,
            serverId,
            deliveryResult,
            updated
                ? AlertDispatchAttemptResult.Delivered(message.Id)
                : AlertDispatchAttemptResult.ClaimLost(message.Id),
            startedTimestamp);
    }

    private async Task<AlertDispatchAttemptResult> ScheduleRetryAsync(
        ClaimedOutboxMessage message,
        Guid? serverId,
        DateTimeOffset completedAtUtc,
        AlertDeliveryAttemptResult deliveryResult,
        long startedTimestamp,
        CancellationToken cancellationToken)
    {
        var delay = deliveryResult.RetryAfter is { } retryAfter &&
            retryAfter >= TimeSpan.Zero &&
            retryAfter <= _settings.MaximumRetryDelay
                ? retryAfter
                : _retryDelayProvider.GetDelay(message.AttemptCount);
        var nextAttemptAtUtc = completedAtUtc + delay;
        var updated = await _outbox.ScheduleRetryAsync(
            message.Id,
            message.ClaimId,
            nextAttemptAtUtc,
            BuildFailureSummary(deliveryResult),
            cancellationToken);

        return CompleteAttempt(
            message,
            serverId,
            deliveryResult,
            updated
                ? AlertDispatchAttemptResult.RetryScheduled(message.Id, nextAttemptAtUtc)
                : AlertDispatchAttemptResult.ClaimLost(message.Id),
            startedTimestamp);
    }

    private async Task<AlertDispatchAttemptResult> MarkDeadLetterAsync(
        ClaimedOutboxMessage message,
        Guid? serverId,
        AlertDeliveryAttemptResult deliveryResult,
        long startedTimestamp,
        CancellationToken cancellationToken)
    {
        var updated = await _outbox.MarkDeadLetterAsync(
            message.Id,
            message.ClaimId,
            BuildFailureSummary(deliveryResult),
            cancellationToken);

        return CompleteAttempt(
            message,
            serverId,
            deliveryResult,
            updated
                ? AlertDispatchAttemptResult.DeadLettered(message.Id)
                : AlertDispatchAttemptResult.ClaimLost(message.Id),
            startedTimestamp);
    }

    private AlertDispatchAttemptResult CompleteAttempt(
        ClaimedOutboxMessage message,
        Guid? serverId,
        AlertDeliveryAttemptResult deliveryResult,
        AlertDispatchAttemptResult result,
        long startedTimestamp)
    {
        var duration = Stopwatch.GetElapsedTime(startedTimestamp);
        GoldSrcOpsMetrics.RecordAlertDeliveryAttempt(ToMetricResult(result.Kind), duration);
        LogDeliveryAttemptCompleted(
            _logger,
            result.Kind == AlertDispatchAttemptResultKind.Delivered
                ? LogLevel.Information
                : LogLevel.Warning,
            message.Id,
            message.EventType,
            serverId,
            message.AggregateId,
            message.AttemptCount,
            message.ClaimId,
            result.Kind,
            deliveryResult.FailureCategory,
            deliveryResult.RemoteStatusCode,
            result.NextAttemptAtUtc,
            duration.TotalMilliseconds);

        return result;
    }

    private static AlertDeliveryMetricResult ToMetricResult(AlertDispatchAttemptResultKind result) =>
        result switch
        {
            AlertDispatchAttemptResultKind.Delivered => AlertDeliveryMetricResult.Delivered,
            AlertDispatchAttemptResultKind.RetryScheduled => AlertDeliveryMetricResult.RetryScheduled,
            AlertDispatchAttemptResultKind.DeadLettered => AlertDeliveryMetricResult.DeadLettered,
            AlertDispatchAttemptResultKind.ClaimLost => AlertDeliveryMetricResult.ClaimLost,
            _ => throw new ArgumentOutOfRangeException(
                nameof(result),
                result,
                "Alert dispatch result is not a completed delivery attempt.")
        };

    private static string BuildFailureSummary(AlertDeliveryAttemptResult result)
    {
        return result.FailureCategory switch
        {
            AlertDeliveryFailureCategory.Transport => "Webhook transport failure.",
            AlertDeliveryFailureCategory.Timeout => "Webhook request timed out.",
            AlertDeliveryFailureCategory.RemoteResponse when result.RemoteStatusCode is { } statusCode =>
                $"Webhook returned HTTP status {statusCode}.",
            AlertDeliveryFailureCategory.RemoteResponse => "Webhook returned an unsuccessful response.",
            AlertDeliveryFailureCategory.Unexpected => "Webhook delivery failed unexpectedly.",
            _ => "Webhook delivery failed."
        };
    }

    private static Guid? TryReadServerId(ClaimedOutboxMessage message)
    {
        if (message.PayloadVersion != IncidentAlertEventV1.CurrentPayloadVersion)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<IncidentAlertEventV1>(
                message.Payload,
                JsonSerializerOptions.Web)?.ServerId;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [LoggerMessage(
        EventId = 3001,
        EventName = "AlertDeliveryAttemptStarted",
        Level = LogLevel.Information,
        Message = "Alert delivery attempt started for event {EventId} of type {EventType}, server {ServerId}, incident {IncidentId}, attempt {AttemptCount}, and claim {ClaimId}.")]
    private static partial void LogDeliveryAttemptStarted(
        ILogger logger,
        Guid eventId,
        string eventType,
        Guid? serverId,
        Guid incidentId,
        int attemptCount,
        Guid claimId);

    [LoggerMessage(
        EventId = 3002,
        EventName = "AlertDeliveryAttemptCompleted",
        Message = "Alert delivery attempt completed for event {EventId} of type {EventType}, server {ServerId}, incident {IncidentId}, attempt {AttemptCount}, claim {ClaimId}, result {DeliveryResult}, failure category {FailureCategory}, remote status {RemoteStatusCode}, next attempt {NextAttemptAtUtc}, and duration {DurationMs} ms.")]
    private static partial void LogDeliveryAttemptCompleted(
        ILogger logger,
        LogLevel level,
        Guid eventId,
        string eventType,
        Guid? serverId,
        Guid incidentId,
        int attemptCount,
        Guid claimId,
        AlertDispatchAttemptResultKind deliveryResult,
        AlertDeliveryFailureCategory? failureCategory,
        int? remoteStatusCode,
        DateTimeOffset? nextAttemptAtUtc,
        double durationMs);

    [LoggerMessage(
        EventId = 3003,
        EventName = "AlertDeliveryAttemptInterrupted",
        Level = LogLevel.Warning,
        Message = "Alert delivery attempt was interrupted for event {EventId} of type {EventType}, server {ServerId}, incident {IncidentId}, attempt {AttemptCount}, claim {ClaimId}, and duration {DurationMs} ms.")]
    private static partial void LogDeliveryAttemptInterrupted(
        ILogger logger,
        Guid eventId,
        string eventType,
        Guid? serverId,
        Guid incidentId,
        int attemptCount,
        Guid claimId,
        double durationMs);

    [LoggerMessage(
        EventId = 3004,
        EventName = "AlertDeliveryAttemptFaulted",
        Level = LogLevel.Error,
        Message = "Alert delivery attempt faulted for event {EventId} of type {EventType}, server {ServerId}, incident {IncidentId}, attempt {AttemptCount}, claim {ClaimId}, failure type {FailureType}, and duration {DurationMs} ms.")]
    private static partial void LogDeliveryAttemptFaulted(
        ILogger logger,
        Guid eventId,
        string eventType,
        Guid? serverId,
        Guid incidentId,
        int attemptCount,
        Guid claimId,
        string failureType,
        double durationMs);
}
