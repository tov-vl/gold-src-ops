using AwesomeAssertions;
using GoldSrcOps.Domain.Servers;

namespace GoldSrcOps.UnitTests.Servers;

public sealed class AvailabilityIncidentTests
{
    [Fact]
    public void Reasons_are_bounded_to_persistence_limit()
    {
        var startReason = new string('s', AvailabilityIncident.MaxReasonLength + 1);
        var endReason = new string('e', AvailabilityIncident.MaxReasonLength + 1);
        var incident = AvailabilityIncident.Open(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            startReason,
            consecutiveFailures: 3);

        incident.Close(DateTimeOffset.UtcNow, endReason);

        incident.StartReason.Should().Be(startReason[..AvailabilityIncident.MaxReasonLength]);
        incident.EndReason.Should().Be(endReason[..AvailabilityIncident.MaxReasonLength]);
    }
}
