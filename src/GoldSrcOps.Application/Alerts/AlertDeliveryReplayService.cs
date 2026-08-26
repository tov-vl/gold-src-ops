using GoldSrcOps.Application.Common;

namespace GoldSrcOps.Application.Alerts;

public sealed class AlertDeliveryReplayService(
    IAlertDeliveryReplayRepository repository,
    IClock clock)
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

    public Task<DeadLetterReplayResult> ReplayAsync(
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

        return repository.ReplayAsync(
            command.RequestId,
            command.EventId,
            requestedBy,
            clock.UtcNow.ToUniversalTime(),
            reason,
            cancellationToken);
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
}
