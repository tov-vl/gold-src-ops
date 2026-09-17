using AwesomeAssertions;
using GoldSrcOps.Api.Endpoints;
using GoldSrcOps.Application.Monitoring;
using Microsoft.AspNetCore.WebUtilities;

namespace GoldSrcOps.UnitTests.Api;

public sealed class OperationsActivityCursorTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Encode_and_decode_round_trip_the_position_and_scope(bool hasServerId)
    {
        var position = new OperationsActivityPagePosition(
            75,
            new DateTimeOffset(2026, 9, 17, 14, 30, 0, TimeSpan.Zero));
        var scope = new OperationsActivityCursorScope(
            25,
            hasServerId ? Guid.Parse("f130f68c-cb3d-4e18-9dfe-7faf62ce8e3f") : null,
            OperationsActivitySource.Gameplay,
            OperationsActivityWindow.Last6Hours);

        var cursor = OperationsActivityCursor.Encode(position, scope);
        var decoded = OperationsActivityCursor.TryDecode(cursor, scope, out var result);

        decoded.Should().BeTrue();
        result.Should().Be(position);
    }

    [Fact]
    public void TryDecode_rejects_a_cursor_from_a_different_scope()
    {
        var position = new OperationsActivityPagePosition(
            50,
            new DateTimeOffset(2026, 9, 17, 14, 30, 0, TimeSpan.Zero));
        var scope = new OperationsActivityCursorScope(
            50,
            null,
            null,
            OperationsActivityWindow.Last24Hours);
        var differentScope = scope with { Source = OperationsActivitySource.Incident };

        var decoded = OperationsActivityCursor.TryDecode(
            OperationsActivityCursor.Encode(position, scope),
            differentScope,
            out var result);

        decoded.Should().BeFalse();
        result.Should().BeNull();
    }

    [Fact]
    public void TryDecode_rejects_an_unknown_version()
    {
        var position = new OperationsActivityPagePosition(
            50,
            new DateTimeOffset(2026, 9, 17, 14, 30, 0, TimeSpan.Zero));
        var scope = new OperationsActivityCursorScope(
            50,
            null,
            null,
            OperationsActivityWindow.Last24Hours);
        var bytes = WebEncoders.Base64UrlDecode(OperationsActivityCursor.Encode(position, scope));
        bytes[0] = byte.MaxValue;

        var decoded = OperationsActivityCursor.TryDecode(
            WebEncoders.Base64UrlEncode(bytes),
            scope,
            out var result);

        decoded.Should().BeFalse();
        result.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-cursor")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void TryDecode_rejects_malformed_values(string cursor)
    {
        var scope = new OperationsActivityCursorScope(
            50,
            null,
            null,
            OperationsActivityWindow.Last24Hours);

        var decoded = OperationsActivityCursor.TryDecode(cursor, scope, out var result);

        decoded.Should().BeFalse();
        result.Should().BeNull();
    }
}
