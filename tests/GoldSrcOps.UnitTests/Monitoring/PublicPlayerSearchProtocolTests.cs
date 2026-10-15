using System.Globalization;
using System.Text;
using AwesomeAssertions;
using GoldSrcOps.Application.Monitoring;
using GoldSrcOps.Infrastructure.Monitoring;
using Microsoft.Extensions.Configuration;

namespace GoldSrcOps.UnitTests.Monitoring;

public sealed class PublicPlayerSearchProtocolTests
{
    internal const string Nonce = "0123456789AbCdEf";
    internal static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    internal static string Frame(string rows = "", int count = 0, bool more = false, DateTimeOffset? at = null, string nonce = Nonce, int version = 1)
    {
        var metadata = FormattableString.Invariant($"{version} {nonce} {(at ?? Now).ToUnixTimeSeconds()} {count} {(more ? 1 : 0)}");
        return $"GSLOOKUP {metadata}\n{rows}GSPEND {metadata}";
    }

    [Theory]
    [InlineData("Кирилл <>&\"")]
    [InlineData("😀")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("\u00a0")]
    public void Complete_frames_preserve_exact_names_and_identical_rows(string name)
    {
        var hex = Convert.ToHexString(Encoding.UTF8.GetBytes(name));
        var rows = string.Concat(Enumerable.Range(1, 10).Select(i => FormattableString.Invariant($"GSPROW {i} 4 1000000000 1000000000 {hex}\n")));
        var result = PublicPlayerSearchProtocol.Parse(Frame(rows, 10, true), Nonce, name, Now);
        result.State.Should().Be(PublicPlayerSearchState.Fresh);
        result.HasMore.Should().BeTrue();
        result.CapturedAtUtc.Should().Be(Now);
        result.Entries.Should().HaveCount(10).And.OnlyContain(row => row.Name == name && row.Kills == 1_000_000_000);
    }

    [Theory]
    [InlineData("GSPROW 2 0 1 0 41\n")]
    [InlineData("GSPROW 1 0 0 0 41\n")]
    [InlineData("GSPROW 1 1 1 0 41\n")]
    [InlineData("GSPROW 1 0 -1 0 41\n")]
    [InlineData("GSPROW 1 4 1000000001 0 41\n")]
    [InlineData("GSPROW 1 0 1 0 C0AF\n")]
    [InlineData("GSPROW 1 0 1 0 EDA080\n")]
    [InlineData("GSPROW 1 0 1 0 F4908080\n")]
    [InlineData("GSPROW 1 0 1 0 C280\n")]
    [InlineData("GSPROW 1 0 1 0 00\n")]
    [InlineData("GSPROW 1 0 1 0 FF\n")]
    [InlineData("GSPROW 1 0 1 0 4\n")]
    [InlineData("GSPROW 1 0 1 0 GG\n")]
    [InlineData("GSPROW 1 0 1 0 -\n")]
    [InlineData("GSPROW 1 0 1 0 61\n")]
    [InlineData("GSPROW 1 0 1 0 2041\n")]
    [InlineData("GSPROW 1 0 1 0 535445414D5F31\n")]
    [InlineData("GSPROW 1 0 1 0 41 extra\n")]
    public void Invalid_rows_fail_without_partial_results(string row)
    {
        var action = () => PublicPlayerSearchProtocol.Parse(Frame(row, 1), Nonce, "A", Now);
        action.Should().Throw<InvalidDataException>().WithMessage("Public player lookup frame is invalid.");
    }

    [Theory]
    [InlineData("nonce")]
    [InlineData("trailer")]
    [InlineData("partial")]
    [InlineData("extra")]
    [InlineData("count")]
    [InlineData("more")]
    [InlineData("future")]
    [InlineData("old")]
    [InlineData("overflow")]
    [InlineData("length")]
    [InlineData("unicode")]
    [InlineData("order")]
    public void Invalid_framing_freshness_and_order_are_rejected(string fault)
    {
        var frame = fault switch
        {
            "nonce" => Frame(nonce: "FFFFFFFFFFFFFFFF"),
            "trailer" => Frame().Replace("GSPEND 1", "GSPEND 2", StringComparison.Ordinal),
            "partial" => Frame().Split('\n')[0],
            "extra" => Frame() + "\nnoise",
            "count" => Frame(count: 1),
            "more" => Frame(more: true),
            "future" => Frame(at: Now.AddSeconds(31)),
            "old" => Frame(at: Now.AddSeconds(-60)),
            "overflow" => Frame().Replace(Now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), "9223372036854775807", StringComparison.Ordinal),
            "length" => new string('A', 2049),
            "unicode" => Frame() + "Я",
            "order" => Frame("GSPROW 1 0 1 1 41\nGSPROW 2 0 1 0 41\n", 2),
            _ => throw new InvalidOperationException()
        };
        var action = () => PublicPlayerSearchProtocol.Parse(frame, Nonce, "A", Now);
        action.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Empty_busy_unavailable_and_old_addon_are_distinct()
    {
        PublicPlayerSearchProtocol.Parse(Frame(), Nonce, "A", Now).State.Should().Be(PublicPlayerSearchState.Fresh);
        PublicPlayerSearchProtocol.Parse($"GSLOOKUP 1 {Nonce} unavailable", Nonce, "A", Now).State.Should().Be(PublicPlayerSearchState.Unavailable);
        PublicPlayerSearchProtocol.Parse($"GSLOOKUP 1 {Nonce} busy 7", Nonce, "A", Now).RetryAfterSeconds.Should().Be(7);
        PublicPlayerSearchProtocol.Parse("GSLOOKUP 1 invalid", Nonce, "A", Now).State.Should().Be(PublicPlayerSearchState.Unavailable);
        foreach (var response in new[] { "Unknown command", $"GSLOOKUP 1 {Nonce} busy 0", $"GSLOOKUP 1 {Nonce} busy 11", "GSLOOKUP 1 FFFFFFFFFFFFFFFF busy 1" })
        {
            var action = () => PublicPlayerSearchProtocol.Parse(response, Nonce, "A", Now);
            action.Should().Throw<InvalidDataException>();
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("A\n")]
    [InlineData("A\u0085")]
    [InlineData("Steam_1")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("ЯЯЯЯЯЯЯЯЯЯЯЯЯЯЯЯ")]
    public void Query_validation_rejects_invalid_names(string? value) => PublicPlayerName.TryNormalize(value, out _).Should().BeFalse();

    [Fact]
    public void Query_validation_is_strict_utf8_and_trims_only_ascii_space()
    {
        PublicPlayerName.TryNormalize(new string('\ud800', 1), out _).Should().BeFalse();
        PublicPlayerName.TryNormalize(" A ", out var ascii).Should().BeTrue();
        ascii.Should().Be("A");
        PublicPlayerName.TryNormalize("\u00a0A\u00a0", out var unicode).Should().BeTrue();
        unicode.Should().Be("\u00a0A\u00a0");
    }

    [Theory]
    [InlineData("[TAG]ALex", "alex", true)]
    [InlineData("КИРИЛЛ", "рил", true)]
    [InlineData("\u0401ЖИК", "\u0451жи", true)]
    [InlineData("\u0401ЖИК", "ежи", false)]
    [InlineData("CAFÉ", "café", false)]
    [InlineData("café", "cafe", false)]
    [InlineData("café", "afe\u0301", false)]
    [InlineData("Straße", "STRASSE", false)]
    [InlineData("ABC", "АВС", false)]
    [InlineData("Игрок", "Igr", false)]
    [InlineData("[TAG]A*B", "a*b", true)]
    [InlineData("[TAG]ABC", "a*c", false)]
    [InlineData("😀😀😀Tail", "😀😀😀", true)]
    public void V2_names_use_the_addon_map_and_preserve_original_spelling(string name, string query, bool matches)
    {
        var row = $"GSPROW 1 0 1 0 {Convert.ToHexString(Encoding.UTF8.GetBytes(name))}\n";
        var parse = () => PublicPlayerSearchProtocol.Parse(Frame(row, 1, version: 2), Nonce, query, Now, PublicPlayerSearchMode.Contains);
        if (matches) { parse().Entries.Single().Name.Should().Be(name); }
        else { parse.Should().Throw<InvalidDataException>(); }
    }

    [Fact]
    public void V2_checks_every_Russian_pair_and_returns_distinct_actual_names_and_duplicates()
    {
        for (var upper = 0x0410; upper <= 0x042F; upper++)
        {
            PublicPlayerSearchMatching.Matches($"AA{(char)upper}", $"aa{(char)(upper + 32)}", PublicPlayerSearchMode.Contains).Should().BeTrue();
        }
        const string rows = "GSPROW 1 0 2 1 5B5441475D43415345\nGSPROW 2 0 2 2 436173655461696C\nGSPROW 3 0 1 2 43617365\nGSPROW 4 0 1 2 43617365\n";
        var result = PublicPlayerSearchProtocol.Parse(Frame(rows, 4, version: 2), Nonce, "cAs", Now, PublicPlayerSearchMode.Contains);
        result.Entries.Select(x => x.Name).Should().Equal("[TAG]CASE", "CaseTail", "Case", "Case");
    }

    [Theory]
    [InlineData("ab", false)]
    [InlineData("  ab  ", false)]
    [InlineData("иг", false)]
    [InlineData("😀😀", false)]
    [InlineData("😀😀😀", true)]
    [InlineData("игр", true)]
    [InlineData("  abc  ", true)]
    [InlineData("a*b", true)]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", true)]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", false)]
    public void Contains_validates_trimmed_scalars_and_utf8_byte_bound(string query, bool expected) =>
        PublicPlayerSearchMatching.TryNormalize(query, PublicPlayerSearchMode.Contains, out _).Should().Be(expected);

    [Fact]
    public void V2_framing_validation_remains_strict_and_versions_never_fallback()
    {
        var good = Frame("GSPROW 1 0 1 1 416263\n", 1, version: 2);
        var faults = new[]
        {
            good.Replace("GSPEND 2", "GSPEND 1", StringComparison.Ordinal),
            good.Replace(Nonce, "FFFFFFFFFFFFFFFF", StringComparison.Ordinal),
            good.Split('\n')[0], good + "\nnoise", Frame(count: 1, version: 2), Frame(more: true, version: 2),
            Frame(at: Now.AddSeconds(31), version: 2), Frame(at: Now.AddSeconds(-60), version: 2),
            Frame("GSPROW 1 0 1 1 416263\nGSPROW 2 0 1 0 416263\n", 2, version: 2),
            Frame("GSPROW 1 0 0 0 416263\n", 1, version: 2),
            Frame("GSPROW 1 1 1 0 416263\n", 1, version: 2),
            Frame("GSPROW 2 0 1 0 416263\n", 1, version: 2),
            Frame("GSPROW 1 0 1 0 C0AF\n", 1, version: 2),
            Frame("GSPROW 1 0 1 0 535445414D5F416263\n", 1, version: 2),
            Frame("GSPROW 1 0 1 0 414243\n", 1),
            "Unknown command: goldsrcops_player_lookup_v2", $"GSLOOKUP 1 {Nonce} unavailable",
            $"GSLOOKUP 2 {Nonce} busy 0", $"GSLOOKUP 2 {Nonce} busy 11", "GSLOOKUP 2 FFFFFFFFFFFFFFFF busy 1",
            new string('A', 2049), good + "Я"
        };
        foreach (var frame in faults)
        {
            var parse = () => PublicPlayerSearchProtocol.Parse(frame, Nonce, "abc", Now, PublicPlayerSearchMode.Contains);
            parse.Should().Throw<InvalidDataException>();
        }
        var wrongVersion = () => PublicPlayerSearchProtocol.Parse(good, Nonce, "Abc", Now);
        wrongVersion.Should().Throw<InvalidDataException>();
        PublicPlayerSearchProtocol.Parse(Frame(version: 2), Nonce, "abc", Now, PublicPlayerSearchMode.Contains).State.Should().Be(PublicPlayerSearchState.Fresh);
        PublicPlayerSearchProtocol.Parse($"GSLOOKUP 2 {Nonce} busy 7", Nonce, "abc", Now, PublicPlayerSearchMode.Contains).RetryAfterSeconds.Should().Be(7);
        PublicPlayerSearchProtocol.Parse($"GSLOOKUP 2 {Nonce} unavailable", Nonce, "abc", Now, PublicPlayerSearchMode.Contains).State.Should().Be(PublicPlayerSearchState.Unavailable);
        PublicPlayerSearchProtocol.Parse("GSLOOKUP 2 invalid", Nonce, "abc", Now, PublicPlayerSearchMode.Contains).State.Should().Be(PublicPlayerSearchState.Unavailable);
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("false", null, false)]
    [InlineData("true", "11111111-1111-1111-1111-111111111111", true)]
    public void Configuration_defaults_off_and_binds_only_the_advertised_server(string? enabled, string? id, bool expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PublicPlayerSearch:Enabled"] = enabled,
            ["PublicServerJoin:ServerId"] = id,
            ["PublicPlayerSearch:ServerId"] = Guid.NewGuid().ToString()
        }).Build();
        var result = PublicPlayerSearchOptions.FromConfiguration(config);
        result.Enabled.Should().Be(expected);
        result.ServerId.Should().Be(expected ? Guid.Parse(id!) : Guid.Empty);
    }

    [Theory]
    [InlineData("yes", "11111111-1111-1111-1111-111111111111")]
    [InlineData("true", null)]
    [InlineData("true", "00000000-0000-0000-0000-000000000000")]
    public void Invalid_enabled_configuration_fails_closed(string enabled, string? id)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PublicPlayerSearch:Enabled"] = enabled,
            ["PublicServerJoin:ServerId"] = id
        }).Build();
        var action = () => PublicPlayerSearchOptions.FromConfiguration(config);
        action.Should().Throw<InvalidOperationException>();
    }
}
