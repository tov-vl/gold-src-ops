using System.Text.Json;
using AwesomeAssertions;
using GoldSrcOps.GameEventAgent;

namespace GoldSrcOps.UnitTests.GameEventAgent;

public sealed class AmxModXGameEventProducerTests
{
    private const string FixtureFileName = "77a456f5-e111-4736-a010-649c60e36bc0.json";

    [Fact]
    public void Round_ended_fixture_is_accepted_by_the_strict_spool_contract()
    {
        var path = FixturePath(FixtureFileName);
        var content = File.ReadAllBytes(path);

        var parsed = GameEventSpoolContract.Parse(
            content,
            FixtureFileName,
            new DateTimeOffset(2026, 9, 14, 9, 0, 30, TimeSpan.Zero));

        content.Should().HaveCountLessThanOrEqualTo(GameEventSpoolContract.MaximumRecordBytes);
        parsed.RecordId.Should().Be(Guid.Parse("77a456f5-e111-4736-a010-649c60e36bc0"));
        parsed.Event.Should().Be(new GameEventSourceInput(
            "round.ended",
            new DateTimeOffset(2026, 9, 14, 9, 0, 0, TimeSpan.Zero),
            "de_dust2",
            Players: 12,
            Bots: 0));

        using var document = JsonDocument.Parse(content);
        document.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .Should().Equal("spoolVersion", "recordId", "event");
        document.RootElement.GetProperty("event").EnumerateObject()
            .Select(property => property.Name)
            .Should().Equal("type", "occurredAtUtc", "map", "players", "bots");
    }

    [Fact]
    public void Producer_is_disabled_by_default_and_has_no_transport_or_identity_surface()
    {
        var source = File.ReadAllText(FixturePath("goldsrcops_game_events.sma"));

        source.Should().Contain("register_cvar(\"goldsrcops_events_enabled\", \"0\"");
        source.Should().Contain("RegisterHookChain(RG_RoundEnd");
        source.Should().Contain("rename_file(temporaryPath, readyPath, true)");
        source.Should().Contain("SetFilePermissions(temporaryPath, FPERM_U_READ | FPERM_U_WRITE)");

        foreach (var forbiddenSurface in new[]
                 {
                     "get_user_authid",
                     "get_user_ip",
                     "get_user_name",
                     "http",
                     "oauth",
                     "rcon",
                     "socket"
                 })
        {
            source.Contains(forbiddenSurface, StringComparison.OrdinalIgnoreCase)
                .Should().BeFalse();
        }
    }

    private static string FixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "GameEventAgent", "Fixtures", fileName);
}
