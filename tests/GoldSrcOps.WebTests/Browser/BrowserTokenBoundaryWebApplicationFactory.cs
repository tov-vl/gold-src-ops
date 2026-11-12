using System.Net;
using System.Security.Claims;
using GoldSrcOps.Contracts.Monitoring;
using GoldSrcOps.Web.Hosting;
using GoldSrcOps.Web.Security;
using GoldSrcOps.Web.Services;
using GoldSrcOps.WebTests.Pages;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GoldSrcOps.WebTests.Browser;

internal sealed class BrowserTokenBoundaryWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string AuthenticationCookieName = "__Host-GoldSrcOps.Web";
    public const string SignInPath = "/test-only/sign-in";
    public const string AccessTokenSentinel =
        "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJicm93c2VyLWFjY2VzcyJ9.c2lnbmF0dXJl";
    public const string IdTokenSentinel =
        "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJicm93c2VyLWlkIn0.c2lnbmF0dXJl";

    private readonly bool searchEnabled;
    private readonly HttpMessageHandler? _gameHostHealthHandler;
    private readonly HttpMessageHandler? _credentialExpiryHandler;
    private readonly HttpMessageHandler? _archiveHealthHandler;
    private readonly HttpMessageHandler? _pollingHealthHandler;
    public PlayerSearchFixtureHandler SearchHandler { get; } = new();
    private readonly bool serverEnabled;
    private readonly PublicServerJoinResponse? serverJoinResponse;
    private readonly HttpStatusCode? serverJoinFailureStatus;
    private readonly PublicLeaderboardResponse? leaderboard;
    private readonly HttpStatusCode? leaderboardFailure;

    public BrowserTokenBoundaryWebApplicationFactory(
        bool serverEnabled = true,
        PublicServerJoinResponse? serverJoinResponse = null,
        HttpStatusCode? serverJoinFailureStatus = null,
        PublicLeaderboardResponse? leaderboard = null,
        HttpStatusCode? leaderboardFailure = null,
        bool searchEnabled = false,
        HttpMessageHandler? gameHostHealthHandler = null,
        HttpMessageHandler? credentialExpiryHandler = null,
        HttpMessageHandler? archiveHealthHandler = null,
        HttpMessageHandler? pollingHealthHandler = null)
    {
        _gameHostHealthHandler = gameHostHealthHandler;
        _credentialExpiryHandler = credentialExpiryHandler;
        _archiveHealthHandler = archiveHealthHandler;
        _pollingHealthHandler = pollingHealthHandler;
        this.searchEnabled = searchEnabled;
        this.serverEnabled = serverEnabled;
        this.serverJoinResponse = serverJoinResponse;
        this.serverJoinFailureStatus = serverJoinFailureStatus;
        this.leaderboard = leaderboard;
        this.leaderboardFailure = leaderboardFailure;
        UseKestrel(0);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseStaticWebAssets();
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["GoldSrcOpsApi:BaseUrl"] = "https://api.example.test/",
                    ["Authentication:Enabled"] = "false",
                    ["PublicPlayerSearch:Enabled"] = searchEnabled.ToString(),
                });
        });
        builder.ConfigureTestServices(services =>
        {
            if (_gameHostHealthHandler is not null)
            {
                services.AddHttpClient<IGameHostHealthClient, GameHostHealthClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => _gameHostHealthHandler);
            }
            if (_credentialExpiryHandler is not null)
            {
                services.AddHttpClient<ICredentialExpiryClient, CredentialExpiryClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => _credentialExpiryHandler);
            }
            if (_archiveHealthHandler is not null)
            {
                services.AddHttpClient<IArchiveHealthClient, ArchiveHealthClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => _archiveHealthHandler);
            }
            if (_pollingHealthHandler is not null)
            {
                services.AddHttpClient<IPollingHealthClient, PollingHealthClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => _pollingHealthHandler);
            }
            services.RemoveAll<WebAuthenticationState>();
            services.AddSingleton(new WebAuthenticationState(Enabled: true));
            services.RemoveAll<IReaderApiClient>();
            services.AddSingleton<IReaderApiClient>(
                new ReaderWebApplicationFactory.FixtureReaderApiClient(serverEnabled));
            services.RemoveAll<IProviderOperationsClient>();
            services.AddSingleton<IProviderOperationsClient>(
                new ReaderWebApplicationFactory.FixtureProviderOperationsClient());
            var publicStatusHandler =
                new PublicDashboardWebApplicationFactory.FixturePublicStatusHandler(
                    statusUnavailable: false,
                    serverJoinResponse: serverJoinResponse,
                    serverJoinFailureStatus: serverJoinFailureStatus,
                    leaderboard: leaderboard, leaderboardFailure: leaderboardFailure);
            services.RemoveAll<PublicPlayerSearchOptions>();
            services.AddSingleton(new PublicPlayerSearchOptions(searchEnabled));
            services.RemoveAll<PublicPlayerSearchClient>();
            services.AddSingleton(provider => new PublicPlayerSearchClient(new HttpClient(SearchHandler)
            {
                BaseAddress = new Uri("https://api.example.test/")
            }, provider.GetRequiredService<PublicApiMetrics>()));
            services.RemoveAll<PublicStatusClient>();
            services.AddSingleton(provider => new PublicStatusClient(new HttpClient(publicStatusHandler)
            {
                BaseAddress = new Uri("https://api.example.test/")
            }, provider.GetRequiredService<PublicApiMetrics>()));
            services.AddSingleton<IStartupFilter, BrowserSignInStartupFilter>();
        });
    }

    private sealed class BrowserSignInStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            application =>
            {
                application.Use(async (context, continuePipeline) =>
                {
                    if (context.Request.Path != SignInPath)
                    {
                        await continuePipeline();
                        return;
                    }

                    var identity = new ClaimsIdentity(
                        [
                            new Claim(ClaimTypes.Name, "Browser boundary user"),
                            new Claim(WebSecurity.SubjectClaim, "browser-boundary-user"),
                            new Claim(ClaimTypes.Role, WebSecurity.OperatorRole),
                        ],
                        CookieAuthenticationDefaults.AuthenticationScheme,
                        ClaimTypes.Name,
                        ClaimTypes.Role);
                    var properties = new AuthenticationProperties
                    {
                        ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5),
                        IsPersistent = false,
                    };
                    properties.StoreTokens(
                        [
                            new AuthenticationToken
                            {
                                Name = "access_token",
                                Value = AccessTokenSentinel,
                            },
                            new AuthenticationToken
                            {
                                Name = "id_token",
                                Value = IdTokenSentinel,
                            },
                        ]);

                    await context.SignInAsync(
                        CookieAuthenticationDefaults.AuthenticationScheme,
                        new ClaimsPrincipal(identity),
                        properties);
                    context.Response.Redirect("/operator/servers");
                });
                next(application);
            };
    }
}
