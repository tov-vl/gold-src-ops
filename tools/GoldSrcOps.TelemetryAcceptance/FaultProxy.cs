using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using GoldSrcOps.Contracts.GameEvents;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace GoldSrcOps.TelemetryAcceptance;

internal sealed class FaultProxy : IAsyncDisposable
{
    private readonly WebApplication _application;
    private readonly HttpClient _api;
    private readonly string _writerToken;
    private readonly string _secret;
    private readonly ConcurrentQueue<DeliveryObservation> _observations = [];
    private int _mode;

    private FaultProxy(WebApplication application, HttpClient api, string writerToken, string secret)
    {
        _application = application;
        _api = api;
        _writerToken = writerToken;
        _secret = secret;
    }

    public Uri Address => new(_application.Urls.Single());
    public DeliveryObservation[] Observations => _observations.ToArray();

    public DeliveryMode Mode
    {
        get => (DeliveryMode)Volatile.Read(ref _mode);
        set => Volatile.Write(ref _mode, (int)value);
    }

    public static async Task<FaultProxy> StartAsync(HttpClient api, Guid serverId, string writerToken, string secret)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0);
            options.Limits.MaxRequestBodySize = 4096;
        });
        var application = builder.Build();
        var proxy = new FaultProxy(application, api, writerToken, secret);
        application.MapPost("/token", proxy.IssueTokenAsync);
        var ingestPath = string.Concat("/api/servers/", serverId.ToString("D"), "/game-events");
        application.MapPost(ingestPath, proxy.ForwardAsync);
        await application.StartAsync(CancellationToken.None).ConfigureAwait(false);
        return proxy;
    }

    public ValueTask DisposeAsync() => _application.DisposeAsync();

    private async Task IssueTokenAsync(HttpContext context)
    {
        var form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
        if (form.Count != 5 || form["grant_type"] != "client_credentials" ||
            form["client_id"] != "acceptance-agent" || form["client_secret"] != _secret ||
            form["audience"] != AcceptanceApi.Audience || form["scope"] != "ingest:game-events")
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        await context.Response.WriteAsJsonAsync(new { access_token = _writerToken, token_type = "Bearer", expires_in = 600 },
            context.RequestAborted).ConfigureAwait(false);
    }

    private async Task ForwardAsync(HttpContext context)
    {
        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer, context.RequestAborted).ConfigureAwait(false);
        var bytes = buffer.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (Mode is DeliveryMode.Unavailable)
        {
            _observations.Enqueue(new DeliveryObservation(hash, 503, Receipt: null));
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, context.Request.Path.Value);
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation("Authorization", context.Request.Headers.Authorization.ToString());
        using var response = await _api.SendAsync(request, context.RequestAborted).ConfigureAwait(false);
        var body = await response.Content.ReadAsByteArrayAsync(context.RequestAborted).ConfigureAwait(false);
        var receipt = response.IsSuccessStatusCode
            ? JsonSerializer.Deserialize<GameEventIngestResponse>(body, JsonSerializerOptions.Web)
            : null;
        var loseAcknowledgement = Mode == DeliveryMode.LoseAcknowledgement && response.StatusCode == HttpStatusCode.Accepted;
        if (loseAcknowledgement)
        {
            Mode = DeliveryMode.Unavailable;
        }

        _observations.Enqueue(new DeliveryObservation(hash, (int)response.StatusCode, receipt));
        if (loseAcknowledgement)
        {
            context.Abort();
            return;
        }

        context.Response.StatusCode = (int)response.StatusCode;
        context.Response.ContentType = "application/json";
        await context.Response.Body.WriteAsync(body, context.RequestAborted).ConfigureAwait(false);
    }
}
