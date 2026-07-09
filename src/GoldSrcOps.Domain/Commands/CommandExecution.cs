using GoldSrcOps.Domain.Servers;

namespace GoldSrcOps.Domain.Commands;

public sealed class CommandExecution
{
    public const int MaxPayloadLength = 2000;
    public const int MaxRequestedByLength = 200;
    public const int MaxResultLength = 2000;

    private CommandExecution()
    {
    }

    public CommandExecution(
        Guid serverId,
        ServerCommandType type,
        string? payload,
        string? requestedBy,
        DateTimeOffset requestedAtUtc)
    {
        if (serverId == Guid.Empty)
        {
            throw new ArgumentException("Server id must not be empty.", nameof(serverId));
        }

        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type), "Command type is not supported.");
        }

        Id = Guid.NewGuid();
        ServerId = serverId;
        Type = type;
        Status = CommandExecutionStatus.Pending;
        Payload = NormalizePayload(type, payload);
        RequestedBy = NormalizeOptionalText(requestedBy, MaxRequestedByLength, nameof(requestedBy));
        RequestedAtUtc = requestedAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid ServerId { get; private set; }

    public ServerCommandType Type { get; private set; }

    public CommandExecutionStatus Status { get; private set; }

    public string? Payload { get; private set; }

    public string? RequestedBy { get; private set; }

    public DateTimeOffset RequestedAtUtc { get; private set; }

    public DateTimeOffset? StartedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public string? ResultSummary { get; private set; }

    public string? FailureReason { get; private set; }

    public Server Server { get; private set; } = null!;

    public void MarkRunning(DateTimeOffset startedAtUtc)
    {
        if (Status != CommandExecutionStatus.Pending)
        {
            throw new InvalidOperationException("Only pending commands can be marked as running.");
        }

        Status = CommandExecutionStatus.Running;
        StartedAtUtc = startedAtUtc;
    }

    public void MarkSucceeded(DateTimeOffset completedAtUtc, string? resultSummary)
    {
        if (Status is not CommandExecutionStatus.Pending and not CommandExecutionStatus.Running)
        {
            throw new InvalidOperationException("Only pending or running commands can be marked as succeeded.");
        }

        Status = CommandExecutionStatus.Succeeded;
        CompletedAtUtc = completedAtUtc;
        ResultSummary = NormalizeOptionalText(resultSummary, MaxResultLength, nameof(resultSummary));
        FailureReason = null;
    }

    public void MarkFailed(DateTimeOffset completedAtUtc, string failureReason)
    {
        if (Status is not CommandExecutionStatus.Pending and not CommandExecutionStatus.Running)
        {
            throw new InvalidOperationException("Only pending or running commands can be marked as failed.");
        }

        Status = CommandExecutionStatus.Failed;
        CompletedAtUtc = completedAtUtc;
        FailureReason = NormalizeRequiredText(failureReason, MaxResultLength, nameof(failureReason));
        ResultSummary = null;
    }

    private static string? NormalizePayload(ServerCommandType type, string? payload)
    {
        if (type == ServerCommandType.Restart)
        {
            return string.IsNullOrWhiteSpace(payload)
                ? null
                : NormalizeRequiredText(payload, MaxPayloadLength, nameof(payload));
        }

        return NormalizeRequiredText(payload, MaxPayloadLength, nameof(payload));
    }

    private static string NormalizeRequiredText(string? value, int maxLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        var normalized = value.Trim();
        if (normalized.Length > maxLength)
        {
            throw new ArgumentException($"Value must not exceed {maxLength} characters.", parameterName);
        }

        return normalized;
    }

    private static string? NormalizeOptionalText(string? value, int maxLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return NormalizeRequiredText(value, maxLength, parameterName);
    }
}
