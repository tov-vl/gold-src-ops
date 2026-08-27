using System.Diagnostics;
using GoldSrcOps.Application.Common;
using GoldSrcOps.Application.Telemetry;
using Microsoft.Extensions.Logging;

namespace GoldSrcOps.Application.Alerts;

public sealed partial class AlertDeliveryReplayService(
    IAlertDeliveryReplayRepository repository,
    IClock clock,
    ILogger<AlertDeliveryReplayService> logger)
{
    public const int MaxReasonLength = 500;
    public const int MaxRequestedByLength = 200;

    public Task<DeadLetterReplayRecordDto?> GetReplayAsync(
        Guid requestId,
        CancellationToken cancellationToken)
    {
        ValidateIdentifier(requestId, nameof(requestId));
        return repository.GetReplayAsync(requestId, cancellationToken);
    }

    public async Task<DeadLetterReplayResult> ReplayAsync(
        DeadLetterReplayCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateIdentifier(command.RequestId, nameof(command.RequestId));
        ValidateIdentifier(command.EventId, nameof(command.EventId));

        var requestedBy = NormalizeRequiredText(
            command.RequestedBy,
            MaxRequestedByLength,
            nameof(command.RequestedBy));
        var reason = NormalizeRequiredText(
            command.Reason,
            MaxReasonLength,
            nameof(command.Reason));

        var requestedAtUtc = clock.UtcNow.ToUniversalTime();
        var startedTimestamp = Stopwatch.GetTimestamp();
        LogReplayStarted(
            logger,
            command.RequestId,
            command.EventId,
            replayNumber: null,
            replayOutcome: "started");

        try
        {
            var result = await repository.ReplayAsync(
                command.RequestId,
                command.EventId,
                requestedBy,
                requestedAtUtc,
                reason,
                cancellationToken);
            var metricResult = ToMetricResult(result.Kind);

            GoldSrcOpsMetrics.RecordAlertReplayRequest(metricResult);
            var logLevel = CompletionLogLevel(metricResult);
            if (logger.IsEnabled(logLevel))
            {
                var replayNumber = result.Replay?.ReplayNumber;
                var replayOutcome = MetricResultName(metricResult);
                var durationMs = Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;
                LogReplayCompleted(
                    logger,
                    logLevel,
                    command.RequestId,
                    command.EventId,
                    replayNumber,
                    replayOutcome,
                    durationMs);
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (logger.IsEnabled(LogLevel.Warning))
            {
                LogReplayInterrupted(
                    logger,
                    command.RequestId,
                    command.EventId,
                    replayNumber: null,
                    replayOutcome: "ambiguous",
                    Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds);
            }

            throw;
        }
        catch (Exception)
        {
            if (logger.IsEnabled(LogLevel.Error))
            {
                LogReplayFaulted(
                    logger,
                    command.RequestId,
                    command.EventId,
                    replayNumber: null,
                    replayOutcome: "faulted",
                    Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds);
            }

            throw;
        }
    }

    private static AlertReplayMetricResult ToMetricResult(DeadLetterReplayResultKind result)
    {
        return result switch
        {
            DeadLetterReplayResultKind.Accepted => AlertReplayMetricResult.Accepted,
            DeadLetterReplayResultKind.Idempotent => AlertReplayMetricResult.Idempotent,
            DeadLetterReplayResultKind.NewerEventProcessing or
                DeadLetterReplayResultKind.IdempotencyConflict => AlertReplayMetricResult.Conflict,
            DeadLetterReplayResultKind.EventNotFound or
                DeadLetterReplayResultKind.EventNotDeadLetter or
                DeadLetterReplayResultKind.EventNotReplayable => AlertReplayMetricResult.Invalid,
            _ => throw new ArgumentOutOfRangeException(
                nameof(result),
                result,
                "Dead-letter replay result is not supported.")
        };
    }

    private static LogLevel CompletionLogLevel(AlertReplayMetricResult result)
    {
        return result is AlertReplayMetricResult.Accepted or AlertReplayMetricResult.Idempotent
            ? LogLevel.Information
            : LogLevel.Warning;
    }

    private static string MetricResultName(AlertReplayMetricResult result)
    {
        return result switch
        {
            AlertReplayMetricResult.Accepted => "accepted",
            AlertReplayMetricResult.Idempotent => "idempotent",
            AlertReplayMetricResult.Conflict => "conflict",
            AlertReplayMetricResult.Invalid => "invalid",
            _ => throw new ArgumentOutOfRangeException(
                nameof(result),
                result,
                "Alert replay metric result is not supported.")
        };
    }

    public static bool TryNormalizeReason(string? reason, out string normalizedReason)
    {
        normalizedReason = reason?.Trim() ?? string.Empty;
        return normalizedReason.Length is >= 1 and <= MaxReasonLength;
    }

    private static void ValidateIdentifier(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Identifier must not be empty.", parameterName);
        }
    }

    private static string NormalizeRequiredText(
        string? value,
        int maxLength,
        string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        var normalized = value.Trim();
        if (normalized.Length > maxLength)
        {
            throw new ArgumentException(
                $"Value must not exceed {maxLength} characters.",
                parameterName);
        }

        return normalized;
    }

    [LoggerMessage(
        EventId = 3101,
        EventName = "AlertReplayStarted",
        Level = LogLevel.Information,
        Message = "Dead-letter replay request {RequestId} for event {EventId} started with replay number {ReplayNumber} and outcome {ReplayOutcome}.")]
    private static partial void LogReplayStarted(
        ILogger logger,
        Guid requestId,
        Guid eventId,
        int? replayNumber,
        string replayOutcome);

    [LoggerMessage(
        EventId = 3102,
        EventName = "AlertReplayCompleted",
        Message = "Dead-letter replay request {RequestId} for event {EventId} completed with replay number {ReplayNumber}, outcome {ReplayOutcome}, and duration {DurationMs} ms.")]
    private static partial void LogReplayCompleted(
        ILogger logger,
        LogLevel level,
        Guid requestId,
        Guid eventId,
        int? replayNumber,
        string replayOutcome,
        double durationMs);

    [LoggerMessage(
        EventId = 3103,
        EventName = "AlertReplayInterrupted",
        Level = LogLevel.Warning,
        Message = "Dead-letter replay request {RequestId} for event {EventId} was interrupted with replay number {ReplayNumber}, outcome {ReplayOutcome}, and duration {DurationMs} ms.")]
    private static partial void LogReplayInterrupted(
        ILogger logger,
        Guid requestId,
        Guid eventId,
        int? replayNumber,
        string replayOutcome,
        double durationMs);

    [LoggerMessage(
        EventId = 3104,
        EventName = "AlertReplayFaulted",
        Level = LogLevel.Error,
        Message = "Dead-letter replay request {RequestId} for event {EventId} faulted with replay number {ReplayNumber}, outcome {ReplayOutcome}, and duration {DurationMs} ms.")]
    private static partial void LogReplayFaulted(
        ILogger logger,
        Guid requestId,
        Guid eventId,
        int? replayNumber,
        string replayOutcome,
        double durationMs);
}
