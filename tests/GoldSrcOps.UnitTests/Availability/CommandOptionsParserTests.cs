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

    [Fact]
    public void TryParse_accepts_the_archive_command_without_credentials()
    {
        var parsed = CommandOptionsParser.TryParse(
            ["archive", "--input", "segment.jsonl"],
            out var options,
            out var error);

        parsed.Should().BeTrue(error);
        options.Should().Be(new ArchiveCommandOptions("segment.jsonl"));
    }

    [Fact]
    public void TryParse_normalizes_the_rehearsal_digest()
    {
        var parsed = CommandOptionsParser.TryParse(
            [
                "rehearse",
                "--sha256", new string('A', 64),
                "--download-output", "downloaded.jsonl",
                "--expected-report", "expected.json",
                "--output", "actual.json",
                "--window-start", "2026-09-04T12:00:00Z",
                "--window-end", "2026-09-04T13:00:00Z",
                "--evaluated-at", "2026-09-04T13:05:00Z",
                "--monitor-revision", "v2-4-shadow-001",
                "--location", "region-a",
            ],
            out var options,
            out var error);

        parsed.Should().BeTrue(error);
        options.Should().BeOfType<RehearseCommandOptions>()
            .Which.Sha256.Should().Be(new string('a', 64));
    }

    [Fact]
    public void TryParse_rejects_archive_credentials_on_the_command_line()
    {
        var parsed = CommandOptionsParser.TryParse(
            ["archive", "--input", "segment.jsonl", "--application-key", "secret"],
            out var options,
            out var error);

        parsed.Should().BeFalse();
        options.Should().BeNull();
        error.Should().Contain("Unknown option --application-key");
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
