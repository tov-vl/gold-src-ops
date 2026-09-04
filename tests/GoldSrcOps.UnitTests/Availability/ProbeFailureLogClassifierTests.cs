using System.Text.Json;
using AwesomeAssertions;
using GoldSrcOps.Application.Availability;
using GoldSrcOps.AvailabilityExporter;

namespace GoldSrcOps.UnitTests.Availability;

public sealed class ProbeFailureLogClassifierTests
{
    public static TheoryData<string, ProbeFailureKind?> FailureCases => LoadFailureCases();

    [Theory]
    [MemberData(nameof(FailureCases))]
    public void Classify_maps_only_known_unambiguous_failure_details(
        string line,
        ProbeFailureKind? expectedFailureKind)
    {
        var result = ProbeFailureLogClassifier.Classify(line);

        result.Should().Be(expectedFailureKind);
    }

    private static TheoryData<string, ProbeFailureKind?> LoadFailureCases()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Availability",
            "Fixtures",
            "loki-failure-cases.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var cases = new TheoryData<string, ProbeFailureKind?>();

        foreach (var item in document.RootElement.EnumerateArray())
        {
            var line = item.GetProperty("line").GetString()
                ?? throw new InvalidDataException("The fixture log line is missing.");
            var expected = item.GetProperty("expectedFailureKind");
            cases.Add(
                line,
                expected.ValueKind == JsonValueKind.Null
                    ? null
                    : ParseFailureKind(expected.GetString()));
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
}
