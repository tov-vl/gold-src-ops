using System.Globalization;
using System.Text.Json;
using GoldSrcOps.Api.Hosting;
using GoldSrcOps.Application.Monitoring;
using GoldSrcOps.Contracts.Monitoring;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Net.Http.Headers;

namespace GoldSrcOps.Api.Endpoints;

public static class PublicPlayerSearchEndpoints
{
    private const string AdmissionPolicy = "PublicPlayerSearch";
    private const int MaximumBodyBytes = 256;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 2 };

    public static IServiceCollection AddPublicPlayerSearchAdmission(this IServiceCollection services)
    {
        services.AddRateLimiter(static options =>
        {
            options.AddFixedWindowLimiter(AdmissionPolicy, static limiter =>
            {
                limiter.PermitLimit = 30;
                limiter.Window = TimeSpan.FromSeconds(10);
                limiter.QueueLimit = 0;
            });
            options.OnRejected = static async (context, token) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.HttpContext.Response.Headers.CacheControl = "no-store";
                context.HttpContext.Response.Headers.RetryAfter = "10";
                await context.HttpContext.Response.WriteAsJsonAsync(Map(PublicPlayerSearchResult.Busy(10)), token);
            };
        });
        return services;
    }

    public static IEndpointRouteBuilder MapPublicPlayerSearchEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/public/players/search", SearchAsync)
            .AllowAnonymous()
            .RequireRateLimiting(AdmissionPolicy)
            .WithTags("Public")
            .WithName("SearchPublicPlayers")
            .Produces<PublicPlayerSearchResponse>()
            .Produces<PublicPlayerSearchResponse>(400)
            .Produces<PublicPlayerSearchResponse>(429)
            .Produces<PublicPlayerSearchResponse>(503);
        return endpoints;
    }

    private static async Task<IResult> SearchAsync(
        HttpContext context,
        PublicServerJoinOptions server,
        PublicPlayerSearchSettings settings,
        PublicPlayerSearchService search,
        IPublicPlayerSearchSource source,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!settings.Enabled || !server.Enabled)
        {
            return TypedResults.NotFound();
        }

        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var mediaType) ||
            !context.Request.HasJsonContentType() ||
            (mediaType.Charset.HasValue && !string.Equals(mediaType.Charset.Value, "utf-8", StringComparison.OrdinalIgnoreCase)))
        {
            return TypedResults.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }

        if (context.Request.ContentLength > MaximumBodyBytes)
        {
            return TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        PublicPlayerSearchRequest? request;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var bodyCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            // Read one extra byte to bound chunked bodies as well as Content-Length requests.
            var body = new byte[MaximumBodyBytes + 1];
            var length = 0;
            while (length < body.Length)
            {
                var read = await context.Request.Body.ReadAsync(body.AsMemory(length), bodyCancellation.Token);
                if (read == 0)
                {
                    break;
                }
                length += read;
            }

            if (length > MaximumBodyBytes)
            {
                return TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            request = JsonSerializer.Deserialize<PublicPlayerSearchRequest>(body.AsSpan(0, length), JsonOptions);
        }
        catch (JsonException)
        {
            return TypedResults.Json(Map(new(PublicPlayerSearchState.Invalid, null, false, [])), statusCode: 400);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            return TypedResults.StatusCode(StatusCodes.Status408RequestTimeout);
        }

        if (!PublicPlayerSearchMatching.TryParseMode(request?.Match, out var mode))
        {
            return TypedResults.Json(Map(new(PublicPlayerSearchState.Invalid, null, false, [])), statusCode: 400);
        }

        var result = await search.SearchAsync(request?.Name, mode, source, cancellationToken);
        if (result.RetryAfterSeconds is { } retry)
        {
            context.Response.Headers.RetryAfter = retry.ToString(CultureInfo.InvariantCulture);
        }

        var status = result.State switch
        {
            PublicPlayerSearchState.Fresh => 200,
            PublicPlayerSearchState.Invalid => 400,
            PublicPlayerSearchState.Busy => 429,
            PublicPlayerSearchState.Disabled => 404,
            _ => 503
        };
        return TypedResults.Json(Map(result), statusCode: status);
    }

    private static PublicPlayerSearchResponse Map(PublicPlayerSearchResult result) => new(
        result.State.ToString().ToLowerInvariant(), result.CapturedAtUtc, result.HasMore,
        result.Entries.Select(static entry => new PublicPlayerSearchEntryResponse(
            entry.Ordinal, entry.Name, entry.Rank, entry.Kills, entry.Deaths)).ToArray(), result.RetryAfterSeconds);
}
