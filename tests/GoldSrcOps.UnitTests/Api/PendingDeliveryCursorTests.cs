using AwesomeAssertions;
using GoldSrcOps.Api.Endpoints;
using GoldSrcOps.Application.Alerts;
using Microsoft.AspNetCore.WebUtilities;

namespace GoldSrcOps.UnitTests.Api;

public sealed class PendingDeliveryCursorTests
{
    [Fact]
    public void Encode_and_decode_round_trip_the_position()
    {
        var nextAttemptAtUtc = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.FromHours(3));
        var position = new PendingDeliveryPagePosition(
            nextAttemptAtUtc,
            nextAttemptAtUtc.AddMinutes(-1),
            Guid.Parse("10203040-5060-7080-90a0-b0c0d0e0f001"));

        var cursor = PendingDeliveryCursor.Encode(position);
        var decoded = PendingDeliveryCursor.TryDecode(cursor, out var result);

        decoded.Should().BeTrue();
        result.Should().Be(new PendingDeliveryPagePosition(
            position.NextAttemptAtUtc.ToUniversalTime(),
            position.OccurredAtUtc.ToUniversalTime(),
            position.EventId));
    }

    [Fact]
    public void TryDecode_rejects_an_unknown_version()
    {
        var now = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
        var position = new PendingDeliveryPagePosition(now, now.AddMinutes(-1), Guid.NewGuid());
        var bytes = WebEncoders.Base64UrlDecode(PendingDeliveryCursor.Encode(position));
        bytes[0] = byte.MaxValue;

        var decoded = PendingDeliveryCursor.TryDecode(
            WebEncoders.Base64UrlEncode(bytes),
            out var result);

        decoded.Should().BeFalse();
        result.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-cursor")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void TryDecode_rejects_malformed_values(string cursor)
    {
        var decoded = PendingDeliveryCursor.TryDecode(cursor, out var result);

        decoded.Should().BeFalse();
        result.Should().BeNull();
    }
}
