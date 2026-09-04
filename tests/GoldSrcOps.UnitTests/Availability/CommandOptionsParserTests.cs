using AwesomeAssertions;
using GoldSrcOps.AvailabilityExporter;

namespace GoldSrcOps.UnitTests.Availability;

public sealed class CommandOptionsParserTests
{
    [Fact]
    public void TryParse_rejects_credentials_on_the_command_line()
    {
        var args = CreateRequiredExportArguments()
            .Concat(["--token", "must-not-be-accepted"])
            .ToArray();

        var parsed = CommandOptionsParser.TryParse(args, out var options, out var error);

        parsed.Should().BeFalse();
        options.Should().BeNull();
        error.Should().Contain("Unknown option --token");
    }

    [Fact]
    public void TryParse_applies_bounded_export_defaults()
    {
        var parsed = CommandOptionsParser.TryParse(
            CreateRequiredExportArguments(),
            out var options,
            out var error);

        parsed.Should().BeTrue(error);
        var export = options.Should().BeOfType<ExportCommandOptions>().Which;
        export.Overlap.Should().Be(TimeSpan.FromMinutes(10));
        export.QueryStep.Should().Be(TimeSpan.FromSeconds(15));
        export.Environment.Should().Be("production");
    }

    private static string[] CreateRequiredExportArguments() =>
    [
        "export",
        "--window-start", "2026-09-04T12:00:00Z",
        "--window-end", "2026-09-04T13:00:00Z",
        "--job", "readiness-job",
        "--probe", "probe-a",
        "--role", "primary",
        "--monitor-revision", "v2-4-shadow-001",
        "--location", "region-a",
        "--output", "segment.jsonl",
    ];
}
