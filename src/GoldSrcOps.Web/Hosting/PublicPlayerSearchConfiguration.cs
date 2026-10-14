using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace GoldSrcOps.Web.Hosting;

public sealed record PublicPlayerSearchOptions(bool Enabled);

public static class PublicPlayerSearchConfiguration
{
    public static void Configure(IServiceCollection services, IConfiguration configuration)
    {
        var configured = configuration["PublicPlayerSearch:Enabled"];
        if (configured is not null && !bool.TryParse(configured, out _))
            throw new InvalidOperationException("PublicPlayerSearch:Enabled must be a boolean.");
        services.AddSingleton(new PublicPlayerSearchOptions(bool.TryParse(configured, out var enabled) && enabled));
    }

    public static IApplicationBuilder UsePublicPlayerSearchBoundary(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (!string.Equals(context.Request.Path.Value?.TrimEnd('/'), "/leaderboard", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }
        var enabled = context.RequestServices.GetRequiredService<PublicPlayerSearchOptions>().Enabled;
        if (enabled)
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
        }
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            await next(context);
            return;
        }
        if (!enabled)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("Not found.", context.RequestAborted);
            return;
        }
        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var mediaType) ||
            !string.Equals(mediaType.MediaType.Value, "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) ||
            (mediaType.Charset.HasValue && !string.Equals(mediaType.Charset.Value, "utf-8", StringComparison.OrdinalIgnoreCase)))
        {
            context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            await context.Response.WriteAsync("Unsupported form content type.", context.RequestAborted);
            return;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        var original = context.Request.Body;
        var bytes = new byte[4097];
        var length = 0;
        try
        {
            if (context.Request.ContentLength is not > 4096)
            {
                while (length < bytes.Length)
                {
                    var read = await original.ReadAsync(bytes.AsMemory(length), deadline.Token);
                    if (read == 0)
                        break;
                    length += read;
                }
            }
            if (context.Request.ContentLength is > 4096 || length > 4096)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                await context.Response.WriteAsync("Form is too large.", context.RequestAborted);
                return;
            }
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            context.Response.StatusCode = StatusCodes.Status408RequestTimeout;
            await context.Response.WriteAsync("Form read timed out.", context.RequestAborted);
            return;
        }
        using var body = new MemoryStream(bytes, 0, length, writable: false);
        context.Request.Body = body;
        context.Features.Set<IFormFeature>(new FormFeature(context.Request, new FormOptions
        {
            KeyLengthLimit = 64,
            ValueLengthLimit = 256,
            ValueCountLimit = 8
        }));
        try
        {
            await next(context);
        }
        finally
        {
            context.Request.Body = original;
        }
    });
}
