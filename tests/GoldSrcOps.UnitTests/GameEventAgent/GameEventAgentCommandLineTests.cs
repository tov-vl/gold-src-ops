using AwesomeAssertions;
using GoldSrcOps.GameEventAgent;
using Microsoft.Extensions.Configuration;

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

    [Fact]
    public void TryParse_accepts_the_access_token_preflight()
    {
        var parsed = GameEventAgentCommandLine.TryParse(
            ["verify-access-token"],
            out var command,
            out var error);

        parsed.Should().BeTrue();
        error.Should().BeNull();
        command.Should().BeOfType<VerifyAccessTokenAgentCommand>();
    }

    [Fact]
    public void Access_token_preflight_enables_delivery_configuration_for_validation()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["GameEventAgent:Delivery:Enabled"] = "false"
            })
            .Build();

        GameEventAgentConsole.PrepareConfigurationForCommand(
            configuration,
            new VerifyAccessTokenAgentCommand());

        configuration["GameEventAgent:Delivery:Enabled"].Should().Be("true");
    }
}
