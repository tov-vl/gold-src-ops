using System.Text;
using AwesomeAssertions;
using GoldSrcOps.Application.Common;
using GoldSrcOps.Application.Monitoring;
using GoldSrcOps.Infrastructure.Monitoring;
using Microsoft.Extensions.Configuration;
using Moq;

namespace GoldSrcOps.UnitTests.Monitoring;

public sealed class PublicLeaderboardTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    internal static string Frame(string rows, int count = 1, DateTimeOffset? timestamp = null)
    {
        var seconds = (timestamp ?? Now).ToUnixTimeSeconds();
        return $"GSLEADER 1 {seconds} {count}\n{rows}GSLEND 1 {seconds} {count}";
    }

    [Fact]
    public void Parser_preserves_unicode_markup_and_authoritative_tie_order_without_identity_fields()
    {
        var hex = Convert.ToHexString(Encoding.UTF8.GetBytes("Кирилл <>&\""));
        var result = PublicLeaderboardProtocol.Parse(Frame($"GSLROW 1 2 120 2 {hex}\nGSLROW 2 2 120 2 -\n", 2), Now);
        result!.Entries.Should().Equal(new PublicLeaderboardEntry(1, "Кирилл <>&\"", 2, 120, 2),
            new PublicLeaderboardEntry(2, "Игрок", 2, 120, 2));
        result.CapturedAtUtc.Should().Be(Now);
    }

    [Fact]
    public void Parser_supports_empty_unavailable_and_counter_ceiling()
    {
        PublicLeaderboardProtocol.Parse(Frame("", 0), Now)!.Entries.Should().BeEmpty();
        PublicLeaderboardProtocol.Parse("GSLEADER 1 unavailable", Now).Should().BeNull();
        PublicLeaderboardProtocol.Parse(Frame("GSLROW 1 4 1000000000 1000000000 41\n"), Now)!
            .Entries.Single().Kills.Should().Be(1_000_000_000);
    }

    [Theory]
    [InlineData("GSLROW 2 0 1 0 41\n")]
    [InlineData("GSLROW 1 0 0 0 41\n")]
    [InlineData("GSLROW 1 0 25 0 41\n")]
    [InlineData("GSLROW 1 0 -1 0 41\n")]
    [InlineData("GSLROW 1 4 1000000001 0 41\n")]
    [InlineData("GSLROW 1 0 1 0 C3\n")]
    [InlineData("GSLROW 1 0 1 0 FF\n")]
    [InlineData("GSLROW 1 0 1 0 00\n")]
    [InlineData("GSLROW 1 0 1 0 20\n")]
    [InlineData("GSLROW 1 0 1 0 4\n")]
    [InlineData("GSLROW 1 0 1 0 GG\n")]
    [InlineData("GSLROW 1 0 1 0 41 extra\n")]
    public void Parser_rejects_invalid_rows_without_partial_publication(string row)
    {
        var action = () => PublicLeaderboardProtocol.Parse(Frame(row), Now);
        action.Should().Throw<InvalidDataException>().WithMessage("Public leaderboard frame is invalid.");
    }

    [Theory]
    [InlineData("GSLEADER 2 1791547200 0\nGSLEND 2 1791547200 0")]
    [InlineData("GSLEADER 1 999999999999999999 0\nGSLEND 1 999999999999999999 0")]
    [InlineData("GSLEADER 1 1 11\nGSLEND 1 1 11")]
    [InlineData("GSLEADER 1 unavailable\nGSLROW 1 0 1 0 41")]
    [InlineData("")]
    public void Parser_rejects_invalid_envelopes(string response)
    {
        var action = () => PublicLeaderboardProtocol.Parse(response, Now);
        action.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Parser_rejects_missing_end_wrong_order_oversized_names_and_bad_clock()
    {
        var valid = Frame("GSLROW 1 0 1 0 41\n");
        var bad = new[]
        {
            valid[..valid.IndexOf("GSLEND", StringComparison.Ordinal)],
            valid + "\nGSLEND 1 1 1",
            Frame("GSLROW 1 0 1 0 41\nGSLROW 2 0 2 0 42\n", 2),
            Frame("GSLROW 1 0 2 2 41\nGSLROW 2 0 2 1 42\n", 2),
            Frame($"GSLROW 1 0 1 0 {new string('4', 64)}\n"),
            Frame("GSLROW 1 0 1 0 41\n", timestamp: Now.AddSeconds(31)),
            Frame("GSLROW 1 0 1 0 41\n", timestamp: Now.AddSeconds(-121)),
            new string('x', 2049)
        };
        foreach (var response in bad)
        {
            var action = () => PublicLeaderboardProtocol.Parse(response, Now);
            action.Should().Throw<InvalidDataException>();
        }
    }

    [Fact]
    public void Parser_suppresses_identity_like_names()
    {
        var hex = Convert.ToHexString(Encoding.UTF8.GetBytes("v1:STEAM_0:1:42"));
        PublicLeaderboardProtocol.Parse(Frame($"GSLROW 1 0 1 0 {hex}\n"), Now)!
            .Entries.Single().Name.Should().Be("Игрок");
    }

    [Fact]
    public void Store_expires_retained_data_and_distinguishes_failures_from_explicit_unavailability()
    {
        var now = Now;
        var clock = new Mock<IClock>();
        clock.SetupGet(x => x.UtcNow).Returns(() => now);
        var store = new PublicLeaderboardStore(clock.Object);
        store.Read().Should().BeEquivalentTo(new PublicLeaderboardProjection("unavailable", null, []));
        PublicLeaderboardEntry[] entries = [new(1, "Игрок", 0, 1, 0)];
        store.Publish(new(Now, entries));
        entries[0] = new(1, "mutated", 0, 2, 0);
        store.Read().State.Should().Be("fresh");
        store.Read().Entries.Single().Name.Should().Be("Игрок");
        store.RecordFailure();
        store.Read().State.Should().Be("stale");
        store.Read().CapturedAtUtc.Should().Be(Now);
        store.Publish(new(Now, []));
        now = Now.AddMinutes(3);
        store.Read().State.Should().Be("fresh");
        now = now.AddTicks(1);
        store.Read().State.Should().Be("stale");
        now = Now.AddDays(1).AddTicks(1);
        store.Read().Entries.Should().BeEmpty();
        store.Read().State.Should().Be("unavailable");
        now = Now;
        store.Publish(new(Now, entries));
        store.RecordFailure(sourceUnavailable: true);
        store.Read().CapturedAtUtc.Should().BeNull();
        store.Read().Entries.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("TRUE", true)]
    public void Options_bind_only_the_advertised_server_and_default_to_disabled(string? value, bool enabled)
    {
        var id = Guid.NewGuid();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PublicLeaderboard:Enabled"] = value,
            ["PublicServerJoin:ServerId"] = id.ToString()
        }).Build();
        var result = PublicLeaderboardOptions.FromConfiguration(configuration);
        result.Should().Be(new PublicLeaderboardSettings(enabled, enabled ? id : Guid.Empty));
    }

    [Theory]
    [InlineData("yes", "")]
    [InlineData("true", "")]
    [InlineData("true", "00000000-0000-0000-0000-000000000000")]
    public void Options_fail_closed_on_invalid_enabled_configuration(string enabled, string id)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PublicLeaderboard:Enabled"] = enabled,
            ["PublicServerJoin:ServerId"] = id
        }).Build();
        var action = () => PublicLeaderboardOptions.FromConfiguration(configuration);
        action.Should().Throw<InvalidOperationException>();
    }
}
