using GoldSrcOps.Application.Monitoring;
using GoldSrcOps.Infrastructure.Monitoring;

namespace GoldSrcOps.Api.Hosting;

internal static class GameHostHealthConfiguration
{
    public static void AddGameHostHealthSource(this IServiceCollection services, IConfiguration configuration)
    {
        Uri? baseAddress = null;
        if (configuration.GetValue<bool>("GameHostHealth:Enabled"))
        {
            var endpoint = configuration["GameHostHealth:PrometheusBaseUrl"];
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out baseAddress) ||
                baseAddress.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(baseAddress.UserInfo) ||
                !string.Equals(baseAddress.AbsolutePath, "/", StringComparison.Ordinal) || !string.IsNullOrEmpty(baseAddress.Query) || !string.IsNullOrEmpty(baseAddress.Fragment))
            {
                throw new InvalidOperationException("GameHostHealth requires a trusted Prometheus origin without credentials or a path.");
            }
        }

        services.AddHttpClient<IGameHostHealthSource, GameHostHealthSource>(client =>
        {
            client.BaseAddress = baseAddress;
            client.Timeout = TimeSpan.FromSeconds(5);
        }).ConfigurePrimaryHttpMessageHandler(static () => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        services.AddHttpClient<IGameHostWarningSource, GameHostWarningSource>(client =>
        {
            client.BaseAddress = baseAddress;
            client.Timeout = TimeSpan.FromSeconds(5);
        }).ConfigurePrimaryHttpMessageHandler(static () => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        services.AddHttpClient<IArchiveHealthSource, ArchiveHealthSource>(client =>
        {
            client.BaseAddress = baseAddress;
            client.Timeout = TimeSpan.FromSeconds(5);
        }).ConfigurePrimaryHttpMessageHandler(static () => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
    }
}
