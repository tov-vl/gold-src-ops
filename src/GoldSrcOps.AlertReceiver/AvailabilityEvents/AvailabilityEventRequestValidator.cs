using GoldSrcOps.Application.Alerts;

namespace GoldSrcOps.AlertReceiver.AvailabilityEvents;

internal static class AvailabilityEventRequestValidator
{
    public const int MaxServerNameLength = 200;
    public const int MaxReasonLength = 2000;

    public static IReadOnlyDictionary<string, string[]> Validate(
        AvailabilityEventRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        AddWhen(request.EventId == Guid.Empty, errors, "eventId", "Event ID must not be empty.");
        AddWhen(request.IncidentId == Guid.Empty, errors, "incidentId", "Incident ID must not be empty.");
        AddWhen(request.ServerId == Guid.Empty, errors, "serverId", "Server ID must not be empty.");
        AddWhen(
            request.PayloadVersion != IncidentAlertEventV1.CurrentPayloadVersion,
            errors,
            "payloadVersion",
            $"Payload version must be {IncidentAlertEventV1.CurrentPayloadVersion}.");
        AddRequiredText(request.ServerName, MaxServerNameLength, errors, "serverName");
        AddRequiredText(request.Reason, MaxReasonLength, errors, "reason");
        AddWhen(
            request.ConsecutiveFailures <= 0,
            errors,
            "consecutiveFailures",
            "Consecutive failures must be positive.");
        AddUtc(request.OccurredAtUtc, errors, "occurredAtUtc");
        AddUtc(request.OpenedAtUtc, errors, "openedAtUtc");

        if (request.ClosedAtUtc is { } closedAtUtc)
        {
            AddUtc(closedAtUtc, errors, "closedAtUtc");
        }

        switch (request.EventType)
        {
            case IncidentAlertEvents.ServerUnavailable:
                ValidateUnavailable(request, errors);
                break;
            case IncidentAlertEvents.ServerRecovered:
                ValidateRecovered(request, errors);
                break;
            default:
                AddWhen(
                    condition: true,
                    errors,
                    "eventType",
                    "Event type is not supported.");
                break;
        }

        return errors.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.ToArray(),
            StringComparer.Ordinal);
    }

    private static void ValidateUnavailable(
        AvailabilityEventRequest request,
        IDictionary<string, List<string>> errors)
    {
        AddWhen(
            request.OccurredAtUtc != request.OpenedAtUtc,
            errors,
            "occurredAtUtc",
            "Unavailable occurrence time must equal the incident open time.");
        AddWhen(
            request.ClosedAtUtc is not null,
            errors,
            "closedAtUtc",
            "Unavailable events must not include a close time.");
        AddWhen(
            request.DurationSeconds is not null,
            errors,
            "durationSeconds",
            "Unavailable events must not include a duration.");
    }

    private static void ValidateRecovered(
        AvailabilityEventRequest request,
        IDictionary<string, List<string>> errors)
    {
        if (request.ClosedAtUtc is not { } closedAtUtc)
        {
            AddWhen(
                condition: true,
                errors,
                "closedAtUtc",
                "Recovered events must include a close time.");
            return;
        }

        AddWhen(
            request.OccurredAtUtc != closedAtUtc,
            errors,
            "occurredAtUtc",
            "Recovered occurrence time must equal the incident close time.");
        AddWhen(
            closedAtUtc < request.OpenedAtUtc,
            errors,
            "closedAtUtc",
            "Incident close time must not precede its open time.");

        if (closedAtUtc >= request.OpenedAtUtc)
        {
            var expectedDuration = Math.Max(
                0L,
                (long)(closedAtUtc - request.OpenedAtUtc).TotalSeconds);
            AddWhen(
                request.DurationSeconds != expectedDuration,
                errors,
                "durationSeconds",
                "Duration must match the bounded incident interval in whole seconds.");
        }
    }

    private static void AddRequiredText(
        string? value,
        int maxLength,
        IDictionary<string, List<string>> errors,
        string field)
    {
        AddWhen(string.IsNullOrWhiteSpace(value), errors, field, "Value is required.");
        if (!string.IsNullOrEmpty(value))
        {
            AddWhen(
                !string.Equals(value, value.Trim(), StringComparison.Ordinal),
                errors,
                field,
                "Value must not have leading or trailing whitespace.");
            AddWhen(
                value.Length > maxLength,
                errors,
                field,
                $"Value must not exceed {maxLength} characters.");
        }
    }

    private static void AddUtc(
        DateTimeOffset value,
        IDictionary<string, List<string>> errors,
        string field) =>
        AddWhen(
            value.Offset != TimeSpan.Zero,
            errors,
            field,
            "Timestamp must use the UTC offset.");

    private static void AddWhen(
        bool condition,
        IDictionary<string, List<string>> errors,
        string field,
        string error)
    {
        if (!condition)
        {
            return;
        }

        if (!errors.TryGetValue(field, out var fieldErrors))
        {
            fieldErrors = [];
            errors.Add(field, fieldErrors);
        }

        fieldErrors.Add(error);
    }
}
