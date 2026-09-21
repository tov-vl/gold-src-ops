using AwesomeAssertions;
using GoldSrcOps.AlertReceiver.ProviderOperations;

namespace GoldSrcOps.AlertReceiver.Tests.ProviderOperations;

public sealed class ProviderDeadLetterCursorTests
{
    [Fact]
    public void Round_trip_preserves_utc_position()
    {
        var position = new ProviderDeadLetterPagePosition(
            new DateTimeOffset(2026, 9, 21, 8, 30, 45, TimeSpan.FromHours(3)),
            Guid.Parse("768c9550-c46d-489d-95fa-2759e8d6a9cd"));

        var cursor = ProviderDeadLetterCursor.Encode(position);
        var decoded = ProviderDeadLetterCursor.TryDecode(cursor, out var result);

        decoded.Should().BeTrue();
        result.Should().NotBeNull();
        result!.DeadLetteredAtUtc.Should().Be(position.DeadLetteredAtUtc.ToUniversalTime());
        result.MessageId.Should().Be(position.MessageId);
        ProviderDeadLetterCursor.Encode(result).Should().Be(cursor);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-cursor")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Malformed_or_unsupported_cursor_is_rejected(string cursor)
    {
        ProviderDeadLetterCursor.TryDecode(cursor, out var position).Should().BeFalse();
        position.Should().BeNull();
    }

    [Fact]
    public void Empty_message_id_cannot_be_encoded()
    {
        var position = new ProviderDeadLetterPagePosition(
            DateTimeOffset.UnixEpoch,
            Guid.Empty);

        var act = () => ProviderDeadLetterCursor.Encode(position);

        act.Should().Throw<ArgumentException>();
    }
}
