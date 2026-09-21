using AwesomeAssertions;
using GoldSrcOps.Web.Hosting;
using GoldSrcOps.Web.Security;
using GoldSrcOps.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace GoldSrcOps.WebTests.Hosting;

public sealed class WebHostingConfigurationTests
{
    [Fact]
    public void Authentication_configures_code_flow_and_server_side_session_cookie()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        var enabled = WebSecurityConfiguration.Configure(
            services,
            CreateConfiguration(
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["Authentication:Enabled"] = "true",
                    ["Authentication:Authority"] = "https://identity.example.test/",
                    ["Authentication:Audience"] = "goldsrcops-api",
                    ["Authentication:ClientId"] = "goldsrcops-web",
                    ["Authentication:ClientSecret"] = "test-client-secret",
                    ["Authentication:RoleClaimType"] = "roles"
                }),
            new TestHostEnvironment(Environments.Development));
        using var serviceProvider = services.BuildServiceProvider();

        var cookie = serviceProvider
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var oidc = serviceProvider
            .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(OpenIdConnectDefaults.AuthenticationScheme);

        enabled.Should().BeTrue();
        serviceProvider.GetRequiredService<WebAuthenticationState>().Enabled.Should().BeTrue();
        cookie.Cookie.HttpOnly.Should().BeTrue();
        cookie.Cookie.SameSite.Should().Be(SameSiteMode.Lax);
        cookie.ExpireTimeSpan.Should().Be(TimeSpan.FromMinutes(55));
        cookie.SlidingExpiration.Should().BeFalse();
        cookie.SessionStore.Should().BeOfType<InMemoryTicketStore>();
        oidc.Authority.Should().Be("https://identity.example.test/");
        oidc.ResponseType.Should().Be(OpenIdConnectResponseType.Code);
        oidc.UsePkce.Should().BeTrue();
        oidc.SaveTokens.Should().BeTrue();
        oidc.CallbackPath.Value.Should().Be("/signin-oidc");
        oidc.SignedOutCallbackPath.Value.Should().Be("/signout-callback-oidc");
    }

    [Fact]
    public void Authentication_rejects_direct_client_secret_in_production()
    {
        var configuration = CreateConfiguration(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Authentication:Enabled"] = "true",
                ["Authentication:Authority"] = "https://identity.example.test/",
                ["Authentication:Audience"] = "goldsrcops-api",
                ["Authentication:ClientId"] = "goldsrcops-web",
                ["Authentication:ClientSecret"] = "direct-secret",
                ["Authentication:RoleClaimType"] = "roles"
            });

        var action = () => WebSecurityConfiguration.Configure(
            new ServiceCollection(),
            configuration,
            new TestHostEnvironment(Environments.Production));

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*file-backed outside Development*");
    }

    [Fact]
    public void DataProtection_requires_persistent_keys_for_authenticated_production_host()
    {
        var action = () => WebDataProtectionConfiguration.Configure(
            new ServiceCollection(),
            CreateConfiguration([]),
            new TestHostEnvironment(Environments.Production),
            authenticationEnabled: true);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*requires a persistent Data Protection key ring*");
    }

    [Fact]
    public void Provider_operations_are_disabled_without_a_secret_by_default()
    {
        var services = new ServiceCollection();

        var enabled = ProviderOperationsClientConfiguration.Configure(
            services,
            CreateConfiguration([]),
            new TestHostEnvironment(Environments.Production));
        using var provider = services.BuildServiceProvider();

        enabled.Should().BeFalse();
        provider.GetRequiredService<IProviderOperationsClient>().IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void Provider_operations_configure_exact_server_side_authorization_in_development()
    {
        const string authorization = "Bearer receiver-operations-sentinel";
        var services = new ServiceCollection();
        services.AddLogging();
        var enabled = ProviderOperationsClientConfiguration.Configure(
            services,
            CreateConfiguration(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ProviderOperations:Enabled"] = "true",
                ["ProviderOperations:BaseUrl"] = "https://receiver.example.test/",
                ["ProviderOperations:Authorization"] = authorization,
            }),
            new TestHostEnvironment(Environments.Development));
        using var provider = services.BuildServiceProvider();

        enabled.Should().BeTrue();
        provider.GetRequiredService<IProviderOperationsClient>().IsEnabled.Should().BeTrue();
        using var httpClient = new HttpClient();
        ProviderOperationsClientConfiguration.ConfigureClient(
            httpClient,
            new Uri("https://receiver.example.test/"),
            authorization);
        httpClient.BaseAddress.Should().Be(new Uri("https://receiver.example.test/"));
        httpClient.DefaultRequestHeaders.GetValues("Authorization").Should().ContainSingle()
            .Which.Should().Be(authorization);
    }

    [Fact]
    public void Provider_operations_reject_direct_authorization_in_production()
    {
        var action = () => ProviderOperationsClientConfiguration.Configure(
            new ServiceCollection(),
            CreateConfiguration(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ProviderOperations:Enabled"] = "true",
                ["ProviderOperations:BaseUrl"] = "https://receiver.example.test/",
                ["ProviderOperations:Authorization"] = "direct-secret",
            }),
            new TestHostEnvironment(Environments.Production));

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*file-backed outside Development*");
    }

    private static IConfiguration CreateConfiguration(
        IEnumerable<KeyValuePair<string, string?>> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "GoldSrcOps.WebTests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
