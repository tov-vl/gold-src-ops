using System.Globalization;
using GoldSrcOps.Application.Availability;

namespace GoldSrcOps.AvailabilityExporter;

internal abstract record CommandOptions;

internal sealed record ExportCommandOptions(
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc,
    string Job,
    string Probe,
    string Environment,
    string Role,
    string MonitorRevision,
    string Location,
    string OutputPath,
    TimeSpan Overlap,
    TimeSpan QueryStep) : CommandOptions;

internal sealed record EvaluateCommandOptions(
    string InputPath,
    string? OutputPath,
    AvailabilityEvaluationRequest Request) : CommandOptions;

internal sealed record ArchiveCommandOptions(string InputPath) : CommandOptions;

internal sealed record RehearseCommandOptions(
    string Sha256,
    string DownloadOutputPath,
    string ExpectedReportPath,
    string ReportOutputPath,
    AvailabilityEvaluationRequest Request) : CommandOptions;

internal static class CommandOptionsParser
{
    private static readonly HashSet<string> ExportKeys = new(StringComparer.Ordinal)
    {
        "--window-start",
        "--window-end",
        "--job",
        "--probe",
        "--environment",
        "--role",
        "--monitor-revision",
        "--location",
        "--output",
        "--overlap-minutes",
        "--step-seconds",
    };

    private static readonly HashSet<string> EvaluateKeys = new(StringComparer.Ordinal)
    {
        "--input",
        "--output",
        "--window-start",
        "--window-end",
        "--evaluated-at",
        "--monitor-revision",
        "--location",
        "--grace-minutes",
        "--target",
    };

    private static readonly HashSet<string> ArchiveKeys = new(StringComparer.Ordinal)
    {
        "--input",
    };

    private static readonly HashSet<string> RehearseKeys = new(StringComparer.Ordinal)
    {
        "--sha256",
        "--download-output",
        "--expected-report",
        "--output",
        "--window-start",
        "--window-end",
        "--evaluated-at",
        "--monitor-revision",
        "--location",
        "--grace-minutes",
        "--target",
    };

    public static bool TryParse(string[] args, out CommandOptions? options, out string error)
    {
        ArgumentNullException.ThrowIfNull(args);
        options = null;
        error = string.Empty;

        if (args.Length == 0)
        {
            error = "Expected the export, evaluate, archive, or rehearse command.";
            return false;
        }

        if (!TryParsePairs(args[1..], out var values, out error))
        {
            return false;
        }

        return args[0] switch
        {
            "export" => TryParseExport(values, out options, out error),
            "evaluate" => TryParseEvaluate(values, out options, out error),
            "archive" => TryParseArchive(values, out options, out error),
            "rehearse" => TryParseRehearse(values, out options, out error),
            _ => Fail("Expected the export, evaluate, archive, or rehearse command.", out options, out error),
        };
    }

    private static bool TryParseExport(
        IReadOnlyDictionary<string, string> values,
        out CommandOptions? options,
        out string error)
    {
        options = null;

        if (!ValidateKeys(values, ExportKeys, out error) ||
            !TryGetUtcMinute(values, "--window-start", out var startUtc, out error) ||
            !TryGetUtcMinute(values, "--window-end", out var endUtc, out error) ||
            !TryGetRequired(values, "--job", out var job, out error) ||
            !TryGetRequired(values, "--probe", out var probe, out error) ||
            !TryGetRequired(values, "--role", out var role, out error) ||
            !TryGetRequired(values, "--monitor-revision", out var revision, out error) ||
            !TryGetRequired(values, "--location", out var location, out error) ||
            !TryGetRequired(values, "--output", out var output, out error) ||
            !TryGetInteger(values, "--overlap-minutes", 10, 0, 1_440, out var overlapMinutes, out error) ||
            !TryGetInteger(values, "--step-seconds", 15, 1, 60, out var stepSeconds, out error))
        {
            return false;
        }

        if (endUtc <= startUtc)
        {
            error = "--window-end must be later than --window-start.";
            return false;
        }

        if (role is not ("primary" or "diagnostic"))
        {
            error = "--role must be primary or diagnostic.";
            return false;
        }

        values.TryGetValue("--environment", out var environment);
        environment ??= "production";

        options = new ExportCommandOptions(
            startUtc,
            endUtc,
            job,
            probe,
            environment,
            role,
            revision,
            location,
            output,
            TimeSpan.FromMinutes(overlapMinutes),
            TimeSpan.FromSeconds(stepSeconds));
        error = string.Empty;
        return true;
    }

    private static bool TryParseArchive(
        IReadOnlyDictionary<string, string> values,
        out CommandOptions? options,
        out string error)
    {
        options = null;
        if (!ValidateKeys(values, ArchiveKeys, out error) ||
            !TryGetRequired(values, "--input", out var input, out error))
        {
            return false;
        }

        options = new ArchiveCommandOptions(input);
        error = string.Empty;
        return true;
    }

    private static bool TryParseRehearse(
        IReadOnlyDictionary<string, string> values,
        out CommandOptions? options,
        out string error)
    {
        options = null;

        if (!ValidateKeys(values, RehearseKeys, out error) ||
            !TryGetRequired(values, "--sha256", out var sha256, out error) ||
            !TryGetRequired(values, "--download-output", out var downloadOutput, out error) ||
            !TryGetRequired(values, "--expected-report", out var expectedReport, out error) ||
            !TryGetRequired(values, "--output", out var reportOutput, out error) ||
            !TryGetUtcMinute(values, "--window-start", out var startUtc, out error) ||
            !TryGetUtcMinute(values, "--window-end", out var endUtc, out error) ||
            !TryGetUtc(values, "--evaluated-at", out var evaluatedAtUtc, out error) ||
            !TryGetRequired(values, "--monitor-revision", out var revision, out error) ||
            !TryGetRequired(values, "--location", out var location, out error) ||
            !TryGetInteger(values, "--grace-minutes", 5, 0, 60, out var graceMinutes, out error) ||
            !TryGetDecimal(values, "--target", 0.995m, 0m, 1m, out var target, out error))
        {
            return false;
        }

        if (endUtc <= startUtc)
        {
            error = "--window-end must be later than --window-start.";
            return false;
        }

        try
        {
            sha256 = EvidenceArchive.NormalizeSha256(sha256);
        }
        catch (ArgumentException)
        {
            error = "--sha256 must contain exactly 64 hexadecimal characters.";
            return false;
        }

        options = new RehearseCommandOptions(
            sha256,
            downloadOutput,
            expectedReport,
            reportOutput,
            new AvailabilityEvaluationRequest(
                startUtc,
                endUtc,
                evaluatedAtUtc,
                revision,
                location,
                TimeSpan.FromMinutes(graceMinutes),
                target));
        error = string.Empty;
        return true;
    }

    private static bool TryParseEvaluate(
        IReadOnlyDictionary<string, string> values,
        out CommandOptions? options,
        out string error)
    {
        options = null;

        if (!ValidateKeys(values, EvaluateKeys, out error) ||
            !TryGetRequired(values, "--input", out var input, out error) ||
            !TryGetUtcMinute(values, "--window-start", out var startUtc, out error) ||
            !TryGetUtcMinute(values, "--window-end", out var endUtc, out error) ||
            !TryGetUtc(values, "--evaluated-at", out var evaluatedAtUtc, out error) ||
            !TryGetRequired(values, "--monitor-revision", out var revision, out error) ||
            !TryGetRequired(values, "--location", out var location, out error) ||
            !TryGetInteger(values, "--grace-minutes", 5, 0, 60, out var graceMinutes, out error) ||
            !TryGetDecimal(values, "--target", 0.995m, 0m, 1m, out var target, out error))
        {
            return false;
        }

        if (endUtc <= startUtc)
        {
            error = "--window-end must be later than --window-start.";
            return false;
        }

        values.TryGetValue("--output", out var output);
        options = new EvaluateCommandOptions(
            input,
            output,
            new AvailabilityEvaluationRequest(
                startUtc,
                endUtc,
                evaluatedAtUtc,
                revision,
                location,
                TimeSpan.FromMinutes(graceMinutes),
                target));
        error = string.Empty;
        return true;
    }

    private static bool TryParsePairs(
        string[] args,
        out IReadOnlyDictionary<string, string> values,
        out string error)
    {
        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);

        if (args.Length % 2 != 0)
        {
            values = parsed;
            error = "Every option must have a value.";
            return false;
        }

        for (var index = 0; index < args.Length; index += 2)
        {
            var key = args[index];
            if (!key.StartsWith("--", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(args[index + 1]))
            {
                values = parsed;
                error = "Options must use --name value pairs.";
                return false;
            }

            if (!parsed.TryAdd(key, args[index + 1]))
            {
                values = parsed;
                error = $"Option {key} was specified more than once.";
                return false;
            }
        }

        values = parsed;
        error = string.Empty;
        return true;
    }

    private static bool ValidateKeys(
        IReadOnlyDictionary<string, string> values,
        HashSet<string> allowed,
        out string error)
    {
        var unknown = values.Keys.FirstOrDefault(key => !allowed.Contains(key));
        if (unknown is not null)
        {
            error = $"Unknown option {unknown}.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryGetRequired(
        IReadOnlyDictionary<string, string> values,
        string key,
        out string value,
        out string error)
    {
        if (values.TryGetValue(key, out value!) && !string.IsNullOrWhiteSpace(value))
        {
            error = string.Empty;
            return true;
        }

        value = string.Empty;
        error = $"Missing required option {key}.";
        return false;
    }

    private static bool TryGetUtcMinute(
        IReadOnlyDictionary<string, string> values,
        string key,
        out DateTimeOffset value,
        out string error)
    {
        if (!TryGetUtc(values, key, out value, out error))
        {
            return false;
        }

        if (value.Second != 0 || value.Millisecond != 0 || value.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            error = $"{key} must be aligned to a UTC minute.";
            return false;
        }

        return true;
    }

    private static bool TryGetUtc(
        IReadOnlyDictionary<string, string> values,
        string key,
        out DateTimeOffset value,
        out string error)
    {
        value = default;

        if (values.TryGetValue(key, out var text) &&
            DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out value) &&
            value.Offset == TimeSpan.Zero)
        {
            error = string.Empty;
            return true;
        }

        error = $"{key} must be an ISO 8601 UTC timestamp ending in Z.";
        return false;
    }

    private static bool TryGetInteger(
        IReadOnlyDictionary<string, string> values,
        string key,
        int defaultValue,
        int minimum,
        int maximum,
        out int value,
        out string error)
    {
        if (!values.TryGetValue(key, out var text))
        {
            value = defaultValue;
            error = string.Empty;
            return true;
        }

        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) &&
            value >= minimum && value <= maximum)
        {
            error = string.Empty;
            return true;
        }

        error = $"{key} must be between {minimum} and {maximum}.";
        return false;
    }

    private static bool TryGetDecimal(
        IReadOnlyDictionary<string, string> values,
        string key,
        decimal defaultValue,
        decimal minimumExclusive,
        decimal maximum,
        out decimal value,
        out string error)
    {
        if (!values.TryGetValue(key, out var text))
        {
            value = defaultValue;
            error = string.Empty;
            return true;
        }

        if (decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value) &&
            value > minimumExclusive && value <= maximum)
        {
            error = string.Empty;
            return true;
        }

        error = $"{key} must be greater than {minimumExclusive} and no greater than {maximum}.";
        return false;
    }

    private static bool Fail(
        string message,
        out CommandOptions? options,
        out string error)
    {
        options = null;
        error = message;
        return false;
    }
}
