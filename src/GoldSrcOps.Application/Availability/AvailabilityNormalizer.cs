using System.Security.Cryptography;
using System.Text;

namespace GoldSrcOps.Application.Availability;

public static class AvailabilityNormalizer
{
    private const string ExecutionIdVersion = "goldsrcops-availability-execution-id-v1";

    public static CanonicalAvailabilityResult Normalize(
        ProviderProbeSample sample,
        AvailabilityNormalizationContext context)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(context);

        ValidateText(context.ProviderName, nameof(context.ProviderName));
        ValidateText(context.MonitorRevision, nameof(context.MonitorRevision));
        ValidateText(context.Location, nameof(context.Location));
        if (!Enum.IsDefined(context.Role))
        {
            throw new ArgumentOutOfRangeException(nameof(context));
        }

        var sourceTimestampUtc = sample.SourceSampleTimestampUtc.ToUniversalTime();
        var scheduledAtUtc = FloorToMinute(sourceTimestampUtc);
        var durationMilliseconds = NormalizeDuration(sample.Duration);
        var httpStatus = NormalizeHttpStatus(sample.HttpStatus);
        var completedAtUtc = sample.CompletedAtUtc?.ToUniversalTime() ?? sourceTimestampUtc;
        var startedAtUtc = sample.StartedAtUtc?.ToUniversalTime();

        if (startedAtUtc is null && sample.Duration is { } duration)
        {
            startedAtUtc = completedAtUtc - duration;
        }

        if (startedAtUtc is { } started && started > completedAtUtc)
        {
            throw new ArgumentException(
                "The probe start timestamp cannot be later than its completion timestamp.",
                nameof(sample));
        }

        return new CanonicalAvailabilityResult(
            scheduledAtUtc,
            startedAtUtc,
            completedAtUtc,
            context.MonitorRevision,
            context.Location,
            context.Role,
            CreateExecutionId(context, scheduledAtUtc, sourceTimestampUtc),
            ClassifyOutcome(sample, httpStatus),
            httpStatus,
            durationMilliseconds);
    }

    internal static DateTimeOffset FloorToMinute(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(
            utc.Year,
            utc.Month,
            utc.Day,
            utc.Hour,
            utc.Minute,
            0,
            TimeSpan.Zero);
    }

    private static AvailabilityOutcome ClassifyOutcome(ProviderProbeSample sample, int? httpStatus)
    {
        if (sample.Succeeded)
        {
            return sample.FailureKind is null && httpStatus is null or 200
                ? AvailabilityOutcome.Good
                : AvailabilityOutcome.MonitorError;
        }

        if (httpStatus is >= 300 and <= 399)
        {
            return AvailabilityOutcome.Redirect;
        }

        if (httpStatus == 200)
        {
            return AvailabilityOutcome.MonitorError;
        }

        if (httpStatus is not null)
        {
            return AvailabilityOutcome.HttpError;
        }

        return sample.FailureKind switch
        {
            ProbeFailureKind.Dns => AvailabilityOutcome.DnsError,
            ProbeFailureKind.Connect => AvailabilityOutcome.ConnectError,
            ProbeFailureKind.Tls => AvailabilityOutcome.TlsError,
            ProbeFailureKind.Timeout => AvailabilityOutcome.Timeout,
            ProbeFailureKind.Monitor or null => AvailabilityOutcome.MonitorError,
            _ => throw new ArgumentOutOfRangeException(nameof(sample)),
        };
    }

    private static int? NormalizeHttpStatus(int? status)
    {
        if (status is null or 0)
        {
            return null;
        }

        if (status is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "HTTP status must be zero, null, or between 100 and 599.");
        }

        return status;
    }

    private static long? NormalizeDuration(TimeSpan? duration)
    {
        if (duration is null)
        {
            return null;
        }

        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        return checked((long)Math.Round(
            duration.Value.TotalMilliseconds,
            MidpointRounding.AwayFromZero));
    }

    private static string CreateExecutionId(
        AvailabilityNormalizationContext context,
        DateTimeOffset scheduledAtUtc,
        DateTimeOffset sourceTimestampUtc)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(ExecutionIdVersion);
            writer.Write(context.ProviderName);
            writer.Write(context.MonitorRevision);
            writer.Write(context.Role.ToString());
            writer.Write(context.Location);
            writer.Write(scheduledAtUtc.UtcDateTime.Ticks);
            writer.Write(sourceTimestampUtc.UtcDateTime.Ticks);
        }

        return $"sha256:{Convert.ToHexString(SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length))))}";
    }

    private static void ValidateText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        if (value.Any(char.IsControl))
        {
            throw new ArgumentException("Control characters are not allowed.", parameterName);
        }
    }
}
