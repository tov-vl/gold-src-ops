using AwesomeAssertions;
using GoldSrcOps.Application.Availability;
using GoldSrcOps.AvailabilityExporter;

namespace GoldSrcOps.UnitTests.Availability;

public sealed class AvailabilityJsonFileTests
{
    [Fact]
    public async Task JsonLines_round_trip_uses_the_canonical_wire_names()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "segment.jsonl");
        var record = CreateRecord();

        try
        {
            await AvailabilityJsonFile.WriteJsonLinesNewAsync(path, [record], CancellationToken.None);

            var json = await File.ReadAllTextAsync(path);
            var restored = await AvailabilityJsonFile.ReadJsonLinesAsync(path, CancellationToken.None);

            json.Should().Contain("\"duration_ms\":250");
            json.Should().Contain("\"outcome\":\"good\"");
            json.Should().Contain("\"role\":\"primary\"");
            json.Should().NotContain("duration_milliseconds");
            restored.Should().ContainSingle().Which.Should().Be(record);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task JsonLines_writer_never_overwrites_an_existing_segment()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "segment.jsonl");
        var record = CreateRecord();

        try
        {
            await AvailabilityJsonFile.WriteJsonLinesNewAsync(path, [record], CancellationToken.None);
            var original = await File.ReadAllTextAsync(path);
            Func<Task> overwrite = () => AvailabilityJsonFile.WriteJsonLinesNewAsync(
                path,
                [record with { ExecutionId = "different" }],
                CancellationToken.None);

            await overwrite.Should().ThrowAsync<IOException>()
                .WithMessage("*create-only*");
            (await File.ReadAllTextAsync(path)).Should().Be(original);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "GoldSrcOps.UnitTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static CanonicalAvailabilityResult CreateRecord()
    {
        var scheduledAtUtc = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
        return new CanonicalAvailabilityResult(
            scheduledAtUtc,
            scheduledAtUtc.AddSeconds(10),
            scheduledAtUtc.AddSeconds(10).AddMilliseconds(250),
            "v2-4-shadow-001",
            "region-a",
            AvailabilityProbeRole.Primary,
            "sha256:fixture",
            AvailabilityOutcome.Good,
            200,
            250);
    }
}
