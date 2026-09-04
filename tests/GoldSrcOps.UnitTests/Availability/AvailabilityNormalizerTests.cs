using System.Text.Json;
using AwesomeAssertions;
using GoldSrcOps.Application.Availability;

namespace GoldSrcOps.UnitTests.Availability;

public sealed class AvailabilityNormalizerTests
{
    private static readonly DateTimeOffset SampleTimestamp =
        new(2026, 9, 4, 12, 34, 27, 250, TimeSpan.Zero);

    public static TheoryData<bool, int?, ProbeFailureKind?, AvailabilityOutcome> NormalizerCases =>
        LoadNormalizerCases();

    [Theory]
    [MemberData(nameof(NormalizerCases))]
    public void Normalize_classifies_contract_fixtures(
        bool succeeded,
        int? httpStatus,
        ProbeFailureKind? failureKind,
        AvailabilityOutcome expectedOutcome)
    {
        var result = AvailabilityNormalizer.Normalize(
            new ProviderProbeSample(
                SampleTimestamp,
                succeeded,
                httpStatus,
                TimeSpan.FromMilliseconds(125),
                failureKind),
            CreateContext());

        result.Outcome.Should().Be(expectedOutcome);
        result.ScheduledAtUtc.Should().Be(new DateTimeOffset(2026, 9, 4, 12, 34, 0, TimeSpan.Zero));
        result.StartedAtUtc.Should().Be(SampleTimestamp - TimeSpan.FromMilliseconds(125));
        result.CompletedAtUtc.Should().Be(SampleTimestamp);
        result.DurationMilliseconds.Should().Be(125);
    }

    [Fact]
    public void Normalize_generates_a_stable_execution_identifier_from_the_source_sample()
    {
        var sample = new ProviderProbeSample(
            SampleTimestamp,
            Succeeded: true,
            HttpStatus: 200,
            Duration: TimeSpan.FromMilliseconds(125));

        var first = AvailabilityNormalizer.Normalize(sample, CreateContext());
        var repeatedExport = AvailabilityNormalizer.Normalize(sample, CreateContext());
        var separateAttempt = AvailabilityNormalizer.Normalize(
            sample with { SourceSampleTimestampUtc = SampleTimestamp.AddSeconds(1) },
            CreateContext());

        repeatedExport.ExecutionId.Should().Be(first.ExecutionId);
        first.ExecutionId.Should().Be(
            "sha256:A7A73317311E5C7F1C0F5CE0EEF64E962E35F4AF1EA39677C4806689DF9F47C0");
        first.ExecutionId.Should().MatchRegex("^sha256:[0-9A-F]{64}$");
        separateAttempt.ExecutionId.Should().NotBe(first.ExecutionId);
    }

    private static AvailabilityNormalizationContext CreateContext() =>
        new("fixture-provider", "v2-4-shadow-001", "region-a", AvailabilityProbeRole.Primary);

    private static TheoryData<bool, int?, ProbeFailureKind?, AvailabilityOutcome> LoadNormalizerCases()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Availability",
            "Fixtures",
            "normalizer-cases.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var cases = new TheoryData<bool, int?, ProbeFailureKind?, AvailabilityOutcome>();

        foreach (var item in document.RootElement.EnumerateArray())
        {
            var succeeded = item.GetProperty("succeeded").GetBoolean();
            var statusElement = item.GetProperty("httpStatus");
            int? httpStatus = statusElement.ValueKind == JsonValueKind.Null
                ? null
                : statusElement.GetInt32();
            var failureElement = item.GetProperty("failureKind");
            ProbeFailureKind? failureKind = failureElement.ValueKind == JsonValueKind.Null
                ? null
                : ParseFailureKind(failureElement.GetString());
            var outcome = ParseOutcome(item.GetProperty("expectedOutcome").GetString());

            cases.Add(succeeded, httpStatus, failureKind, outcome);
        }

        return cases;
    }

    private static ProbeFailureKind ParseFailureKind(string? value) => value switch
    {
        "dns" => ProbeFailureKind.Dns,
        "connect" => ProbeFailureKind.Connect,
        "tls" => ProbeFailureKind.Tls,
        "timeout" => ProbeFailureKind.Timeout,
        "monitor" => ProbeFailureKind.Monitor,
        _ => throw new InvalidDataException("The fixture contains an unknown failure kind."),
    };

    private static AvailabilityOutcome ParseOutcome(string? value) => value switch
    {
        "good" => AvailabilityOutcome.Good,
        "dns_error" => AvailabilityOutcome.DnsError,
        "connect_error" => AvailabilityOutcome.ConnectError,
        "tls_error" => AvailabilityOutcome.TlsError,
        "timeout" => AvailabilityOutcome.Timeout,
        "redirect" => AvailabilityOutcome.Redirect,
        "http_error" => AvailabilityOutcome.HttpError,
        "monitor_error" => AvailabilityOutcome.MonitorError,
        _ => throw new InvalidDataException("The fixture contains an unknown outcome."),
    };
}
