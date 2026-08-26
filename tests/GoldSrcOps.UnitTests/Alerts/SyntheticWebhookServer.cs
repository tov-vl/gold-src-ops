using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GoldSrcOps.UnitTests.Alerts;

internal sealed class SyntheticWebhookServer : IAsyncDisposable
{
    private readonly WebApplication _application;

    private SyntheticWebhookServer(WebApplication application, Uri baseAddress)
    {
        _application = application;
        BaseAddress = baseAddress;
    }

    public Uri BaseAddress { get; }

    public static async Task<SyntheticWebhookServer> StartAsync(RequestDelegate handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));

        var application = builder.Build();
        application.Run(handler);
        await application.StartAsync(CancellationToken.None).ConfigureAwait(false);

        var server = application.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
        var address = addresses?.SingleOrDefault()
            ?? throw new InvalidOperationException("Synthetic webhook server did not publish an address.");

        return new SyntheticWebhookServer(application, new Uri(address, UriKind.Absolute));
    }

    public Uri GetUri(string relativePath) => new(BaseAddress, relativePath);

    public async ValueTask DisposeAsync()
    {
        await _application.StopAsync(CancellationToken.None).ConfigureAwait(false);
        await _application.DisposeAsync().ConfigureAwait(false);
    }
}
