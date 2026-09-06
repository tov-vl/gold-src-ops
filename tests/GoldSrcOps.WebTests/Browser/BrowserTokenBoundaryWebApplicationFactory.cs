using System.Security.Claims;
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

    public BrowserTokenBoundaryWebApplicationFactory()
    {
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
                });
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<WebAuthenticationState>();
            services.AddSingleton(new WebAuthenticationState(Enabled: true));
            services.RemoveAll<IReaderApiClient>();
            services.AddSingleton<IReaderApiClient, ReaderWebApplicationFactory.FixtureReaderApiClient>();
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
                            new Claim(ClaimTypes.Role, WebSecurity.ReaderRole),
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
