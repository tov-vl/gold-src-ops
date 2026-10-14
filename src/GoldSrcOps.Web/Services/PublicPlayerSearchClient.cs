using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using GoldSrcOps.Contracts.Monitoring;

namespace GoldSrcOps.Web.Services;

public sealed class PublicPlayerSearchClient(HttpClient httpClient, PublicApiMetrics metrics)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static PublicPlayerSearchResponse Unavailable => new("unavailable", null, false, [], null);

    public static bool TryNormalize(string? value, out string name)
    {
        name = string.Empty;
        if (value is null || value.Length > 256 || value.Any(char.IsControl) ||
            value.Contains("STEAM_", StringComparison.OrdinalIgnoreCase))
            return false;
        var trimmed = value.Trim(' ');
        try
        {
            if (StrictUtf8.GetByteCount(trimmed) is < 1 or > 31)
                return false;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
        name = trimmed;
        return true;
    }

    public async Task<PublicPlayerSearchResponse> SearchAsync(string? value, CancellationToken cancellationToken = default)
    {
        if (!TryNormalize(value, out var name))
            return new("invalid", null, false, [], null);
        var started = Stopwatch.GetTimestamp();
        var outcome = "error";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(httpClient.Timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/public/players/search")
            {
                Content = JsonContent.Create(new PublicPlayerSearchRequest(name))
            };
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                outcome = "not_found";
                return Unavailable;
            }
            if (response.Content.Headers.ContentLength is > 4096 ||
                !string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
                return Unavailable;
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            var bytes = new byte[4097];
            var length = 0;
            while (length < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(length), deadline.Token);
                if (read == 0)
                    break;
                length += read;
            }
            deadline.Token.ThrowIfCancellationRequested();
            if (length > 4096)
                return Unavailable;
            var result = JsonSerializer.Deserialize<PublicPlayerSearchResponse>(bytes.AsSpan(0, length), JsonOptions);
            if (!IsValid(result, response.StatusCode, name))
                return Unavailable;
            deadline.Token.ThrowIfCancellationRequested();
            outcome = result!.State switch { "fresh" => "success", "busy" => "busy", _ => "error" };
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = "canceled";
            throw;
        }
        catch (OperationCanceledException)
        {
            outcome = "timeout";
            return Unavailable;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException)
        {
            return Unavailable;
        }
        finally
        {
            metrics.Record("player_search", outcome, Stopwatch.GetElapsedTime(started));
        }
    }

    private static bool IsValid(PublicPlayerSearchResponse? result, HttpStatusCode status, string name)
    {
        if (result?.Entries is null || result.Entries.Count > 10)
            return false;
        if (!string.Equals(result.State, "fresh", StringComparison.Ordinal))
        {
            if (result.Entries.Count != 0 || result.CapturedAtUtc is not null || result.HasMore)
                return false;
            return (status, result.State) switch
            {
                (HttpStatusCode.TooManyRequests, "busy") => result.RetryAfterSeconds is >= 1 and <= 10,
                (HttpStatusCode.BadRequest, "invalid") or (HttpStatusCode.ServiceUnavailable, "unavailable") => result.RetryAfterSeconds is null,
                _ => false
            };
        }
        if (status != HttpStatusCode.OK || result.CapturedAtUtc is not { } captured ||
            captured < DateTimeOffset.UtcNow.AddSeconds(-60) || captured > DateTimeOffset.UtcNow.AddSeconds(30) ||
            result.RetryAfterSeconds is not null || (result.HasMore && result.Entries.Count != 10))
            return false;
        PublicPlayerSearchEntryResponse? previous = null;
        foreach (var entry in result.Entries)
        {
            if (entry is null || entry.Ordinal != (previous?.Ordinal ?? 0) + 1 ||
                !string.Equals(entry.Name, name, StringComparison.Ordinal) ||
                entry.Kills is < 0 or > 1_000_000_000 || entry.Deaths is < 0 or > 1_000_000_000 ||
                (entry.Kills == 0 && entry.Deaths == 0) ||
                entry.Rank != (entry.Kills switch { >= 500 => 4, >= 250 => 3, >= 100 => 2, >= 25 => 1, _ => 0 }) ||
                (previous is not null && (entry.Kills > previous.Kills ||
                    (entry.Kills == previous.Kills && entry.Deaths < previous.Deaths))))
                return false;
            previous = entry;
        }
        return true;
    }
}
