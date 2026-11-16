using System.Text.Json;

namespace GoldSrcOps.TelemetryAcceptance;

internal sealed record NativeInput(string Name, Guid RecordId, byte[] Bytes, string Sha256, JsonElement Event)
{
    public DateTimeOffset OccurredAtUtc => Event.GetProperty("occurredAtUtc").GetDateTimeOffset();
}
