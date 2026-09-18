using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GoldSrcOps.AlertReceiver.AvailabilityEvents;

internal sealed record AvailabilityEventPayload(string Json, string Sha256)
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    public static AvailabilityEventPayload Create(AvailabilityEventRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var json = JsonSerializer.Serialize(request, SerializerOptions);
        var hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(json)))
            .ToLowerInvariant();

        return new AvailabilityEventPayload(json, hash);
    }
}
