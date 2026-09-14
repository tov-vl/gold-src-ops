using AwesomeAssertions;
using GoldSrcOps.GameEventAgent;

namespace GoldSrcOps.UnitTests.GameEventAgent;

public sealed class GameEventAgentCommandLineTests
{
    [Fact]
    public void TryParse_accepts_the_synthetic_spool_writer()
    {
        var parsed = GameEventAgentCommandLine.TryParse(
            ["spool-write", "--file", "event.json"],
            out var command,
            out var error);

        parsed.Should().BeTrue();
        error.Should().BeNull();
        command.Should().Be(new WriteSpoolRecordCommand("event.json"));
    }

    [Fact]
    public void TryParse_accepts_the_one_shot_spool_importer()
    {
        var parsed = GameEventAgentCommandLine.TryParse(
            ["import-spool"],
            out var command,
            out var error);

        parsed.Should().BeTrue();
        error.Should().BeNull();
        command.Should().BeOfType<ImportSpoolCommand>();
    }
}
