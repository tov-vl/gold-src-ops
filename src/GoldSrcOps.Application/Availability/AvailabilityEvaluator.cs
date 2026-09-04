namespace GoldSrcOps.Application.Availability;

public static class AvailabilityEvaluator
{
    public const string CurrentRevision = "availability-evaluator-v1";

    public static AvailabilityEvaluationReport Evaluate(
        IEnumerable<CanonicalAvailabilityResult> records,
        AvailabilityEvaluationRequest request)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        var slotCount = checked((int)((request.WindowEndUtc - request.WindowStartUtc).Ticks /
            TimeSpan.TicksPerMinute));
        var validatedRecords = records.ToArray();
        foreach (var record in validatedRecords)
        {
            ValidateRecord(record);
        }

        var matchingRecords = validatedRecords.Where(record =>
            record.Role == AvailabilityProbeRole.Primary &&
            string.Equals(record.MonitorRevision, request.MonitorRevision, StringComparison.Ordinal) &&
            string.Equals(record.Location, request.Location, StringComparison.Ordinal) &&
            record.ScheduledAtUtc >= request.WindowStartUtc &&
            record.ScheduledAtUtc < request.WindowEndUtc);

        var recordsByExecutionId = new Dictionary<string, CanonicalAvailabilityResult>(StringComparer.Ordinal);
        var duplicateRecordCount = 0;

        foreach (var record in matchingRecords)
        {
            if (recordsByExecutionId.TryGetValue(record.ExecutionId, out var existing))
            {
                if (existing != record)
                {
                    throw new InvalidDataException(
                        "The same execution identifier was associated with conflicting records.");
                }

                duplicateRecordCount++;
                continue;
            }

            recordsByExecutionId.Add(record.ExecutionId, record);
        }

        var recordsBySlot = recordsByExecutionId.Values
            .GroupBy(record => record.ScheduledAtUtc)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var counts = new int[Enum.GetValues<AvailabilityOutcome>().Length];
        var evaluatedSlotCount = 0;
        var ignoredAttemptCount = 0;

        for (var index = 0; index < slotCount; index++)
        {
            var slot = request.WindowStartUtc.AddMinutes(index);
            if (slot + request.MissingGracePeriod > request.EvaluatedAtUtc)
            {
                continue;
            }

            evaluatedSlotCount++;
            if (!recordsBySlot.TryGetValue(slot, out var candidates) || candidates.Length == 0)
            {
                counts[(int)AvailabilityOutcome.Missing]++;
                continue;
            }

            var canonical = candidates
                .OrderBy(record => record.StartedAtUtc ?? record.CompletedAtUtc ?? DateTimeOffset.MaxValue)
                .ThenBy(record => record.CompletedAtUtc ?? DateTimeOffset.MaxValue)
                .ThenBy(record => record.ExecutionId, StringComparer.Ordinal)
                .First();

            counts[(int)canonical.Outcome]++;
            ignoredAttemptCount += candidates.Length - 1;
        }

        var goodSlotCount = counts[(int)AvailabilityOutcome.Good];
        var missingSlotCount = counts[(int)AvailabilityOutcome.Missing];
        var badSlotCount = evaluatedSlotCount - goodSlotCount;
        var allowedBadSlotCount = (int)decimal.Floor(
            evaluatedSlotCount * (1m - request.TargetAvailability));
        decimal? availability = evaluatedSlotCount == 0
            ? null
            : decimal.Round(goodSlotCount / (decimal)evaluatedSlotCount, 9);
        bool? meetsTarget = availability is null || evaluatedSlotCount < slotCount
            ? null
            : availability >= request.TargetAvailability && badSlotCount <= allowedBadSlotCount;

        return new AvailabilityEvaluationReport(
            CurrentRevision,
            request.WindowStartUtc,
            request.WindowEndUtc,
            request.EvaluatedAtUtc,
            request.MonitorRevision,
            request.Location,
            slotCount,
            evaluatedSlotCount,
            slotCount - evaluatedSlotCount,
            goodSlotCount,
            badSlotCount,
            missingSlotCount,
            duplicateRecordCount,
            ignoredAttemptCount,
            availability,
            request.TargetAvailability,
            allowedBadSlotCount,
            meetsTarget,
            new AvailabilityOutcomeCounts(
                counts[(int)AvailabilityOutcome.Good],
                counts[(int)AvailabilityOutcome.DnsError],
                counts[(int)AvailabilityOutcome.ConnectError],
                counts[(int)AvailabilityOutcome.TlsError],
                counts[(int)AvailabilityOutcome.Timeout],
                counts[(int)AvailabilityOutcome.Redirect],
                counts[(int)AvailabilityOutcome.HttpError],
                counts[(int)AvailabilityOutcome.MonitorError],
                counts[(int)AvailabilityOutcome.Missing]));
    }

    private static void ValidateRequest(AvailabilityEvaluationRequest request)
    {
        ValidateMinuteBoundary(request.WindowStartUtc, nameof(request.WindowStartUtc));
        ValidateMinuteBoundary(request.WindowEndUtc, nameof(request.WindowEndUtc));

        if (request.WindowEndUtc <= request.WindowStartUtc)
        {
            throw new ArgumentException("The evaluation window must not be empty.", nameof(request));
        }

        if (request.EvaluatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The evaluation timestamp must use UTC.", nameof(request));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.MonitorRevision, nameof(request));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Location, nameof(request));

        if (request.MissingGracePeriod < TimeSpan.Zero || request.MissingGracePeriod > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        if (request.TargetAvailability is <= 0m or > 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }
    }

    private static void ValidateRecord(CanonicalAvailabilityResult record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(record.ExecutionId, nameof(record));
        ArgumentException.ThrowIfNullOrWhiteSpace(record.MonitorRevision, nameof(record));
        ArgumentException.ThrowIfNullOrWhiteSpace(record.Location, nameof(record));
        ValidateMinuteBoundary(record.ScheduledAtUtc, nameof(record.ScheduledAtUtc));

        if (!Enum.IsDefined(record.Role) || !Enum.IsDefined(record.Outcome))
        {
            throw new InvalidDataException("An availability record has an unknown enum value.");
        }

        if (record.StartedAtUtc?.Offset != TimeSpan.Zero ||
            record.CompletedAtUtc?.Offset != TimeSpan.Zero ||
            record.StartedAtUtc > record.CompletedAtUtc)
        {
            throw new InvalidDataException("An availability record has invalid execution timestamps.");
        }

        if (record.HttpStatus is not null and (< 100 or > 599) || record.DurationMilliseconds < 0)
        {
            throw new InvalidDataException("An availability record has invalid measurement values.");
        }

        var outcomeIsConsistent = record.Outcome switch
        {
            AvailabilityOutcome.Good => record.HttpStatus is null or 200,
            AvailabilityOutcome.Redirect => record.HttpStatus is >= 300 and <= 399,
            AvailabilityOutcome.HttpError => record.HttpStatus is not null and not 200 and not (>= 300 and <= 399),
            AvailabilityOutcome.DnsError or
                AvailabilityOutcome.ConnectError or
                AvailabilityOutcome.TlsError or
                AvailabilityOutcome.Timeout => record.HttpStatus is null,
            AvailabilityOutcome.MonitorError => true,
            AvailabilityOutcome.Missing => false,
            _ => false,
        };

        if (!outcomeIsConsistent)
        {
            throw new InvalidDataException("An availability record has an inconsistent outcome.");
        }
    }

    private static void ValidateMinuteBoundary(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero || value != AvailabilityNormalizer.FloorToMinute(value))
        {
            throw new ArgumentException("The timestamp must be aligned to a UTC minute.", parameterName);
        }
    }
}
