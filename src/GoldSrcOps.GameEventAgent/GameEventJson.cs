using System.Text.Json;
using System.Text.Json.Serialization;
using GoldSrcOps.Contracts.GameEvents;

namespace GoldSrcOps.GameEventAgent;

internal static class GameEventJson
{
    public const int MaximumRequestBytes = 4 * 1024;
    public const int MaximumResponseBytes = 16 * 1024;

    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web);

    public static JsonSerializerOptions StrictSerializerOptions { get; } = new(SerializerOptions)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static byte[] SerializeRequest(GameEventIngestRequest request)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(request, SerializerOptions);
        if (payload.Length > MaximumRequestBytes)
        {
            throw new InvalidOperationException("The serialized game-event request exceeds 4 KiB.");
        }

        return payload;
    }

    public static async Task<T?> ReadBoundedAsync<T>(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken,
        JsonSerializerOptions? serializerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);

        if (content.Headers.ContentLength is long contentLength && contentLength > maximumBytes)
        {
            throw new InvalidDataException("The HTTP response exceeded its configured size limit.");
        }

        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream(Math.Min(maximumBytes, 4 * 1024));
        var chunk = new byte[4 * 1024];

        while (true)
        {
            var read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maximumBytes)
            {
                throw new InvalidDataException("The HTTP response exceeded its configured size limit.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        buffer.Position = 0;
        return await JsonSerializer.DeserializeAsync<T>(
            buffer,
            serializerOptions ?? SerializerOptions,
            cancellationToken).ConfigureAwait(false);
    }
}
