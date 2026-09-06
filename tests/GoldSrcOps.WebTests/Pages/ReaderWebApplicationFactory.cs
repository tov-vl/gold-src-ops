using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using GoldSrcOps.Contracts.Incidents;
using GoldSrcOps.Contracts.Monitoring;
using GoldSrcOps.Contracts.Servers;
using GoldSrcOps.Web.Security;
using GoldSrcOps.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GoldSrcOps.WebTests.Pages;

internal sealed class DisabledAuthenticationWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["GoldSrcOpsApi:BaseUrl"] = "https://api.example.test/",
                    ["Authentication:Enabled"] = "false"
                });
        });
    }
}

internal sealed class ReaderWebApplicationFactory(string? role = WebSecurity.ReaderRole)
    : WebApplicationFactory<Program>
{
    public static readonly Guid ServerId = Guid.Parse("f130f68c-cb3d-4e18-9dfe-7faf62ce8e3f");
    public static readonly Guid OpenIncidentId = Guid.Parse("9307a87e-61cf-4901-8026-b301908431d6");
    public const string ServerName = "Reader fixture server";
    public const string OpenIncidentReason = "A2S query timed out";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["GoldSrcOpsApi:BaseUrl"] = "https://api.example.test/",
                    ["Authentication:Enabled"] = "false"
                });
        });
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
                    options.DefaultForbidScheme = TestAuthenticationHandler.SchemeName;
                })
                .AddScheme<TestAuthenticationOptions, TestAuthenticationHandler>(
                    TestAuthenticationHandler.SchemeName,
                    options => options.Role = role);
            services.RemoveAll<WebAuthenticationState>();
            services.AddSingleton(new WebAuthenticationState(true));
            services.RemoveAll<IReaderApiClient>();
            services.AddSingleton<IReaderApiClient, FixtureReaderApiClient>();
        });
    }

    internal sealed class FixtureReaderApiClient : IReaderApiClient
    {
        private static readonly DateTimeOffset ObservedAtUtc =
            new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

        public Task<DashboardOverviewResponse> GetOverviewAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DashboardOverviewResponse(1, 1, 0, 1, 0, 0, 0, ObservedAtUtc));

        public Task<IReadOnlyList<ServerResponse>> GetServersAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ServerResponse>>([CreateServer()]);

        public Task<ServerResponse?> GetServerAsync(
            Guid serverId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(serverId == ServerId ? CreateServer() : null);

        public Task<ServerStatusResponse?> GetServerStatusAsync(
            Guid serverId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ServerStatusResponse?>(serverId == ServerId
                ? new ServerStatusResponse(
                    ServerId,
                    "Online",
                    true,
                    ObservedAtUtc,
                    ObservedAtUtc,
                    18,
                    "de_dust2",
                    0,
                    20,
                    null,
                    0)
                : null);

        public Task<IReadOnlyList<AvailabilityIncidentResponse>> GetOpenIncidentsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AvailabilityIncidentResponse>>([CreateOpenIncident()]);

        public Task<IReadOnlyList<AvailabilityIncidentResponse>> GetServerIncidentsAsync(
            Guid serverId,
            int limit,
            CancellationToken cancellationToken = default)
        {
            if (serverId != ServerId)
            {
                return Task.FromResult<IReadOnlyList<AvailabilityIncidentResponse>>([]);
            }

            IReadOnlyList<AvailabilityIncidentResponse> incidents =
            [
                CreateOpenIncident(),
                new AvailabilityIncidentResponse(
                    Guid.Parse("5e3fd38c-a3c8-4a2d-a8a2-ac8fd7b9a788"),
                    ServerId,
                    "Unreachable",
                    ObservedAtUtc.AddHours(-3),
                    ObservedAtUtc.AddHours(-2),
                    "Connection refused",
                    "Probe recovered",
                    3)
            ];

            return Task.FromResult<IReadOnlyList<AvailabilityIncidentResponse>>(incidents.Take(limit).ToArray());
        }

        public Task<SnapshotHistoryResponse?> GetServerSnapshotsAsync(
            Guid serverId,
            int limit,
            CancellationToken cancellationToken = default)
        {
            if (serverId != ServerId)
            {
                return Task.FromResult<SnapshotHistoryResponse?>(null);
            }

            PollSnapshotResponse[] snapshots =
            [
                new(
                    Guid.Parse("dbe590f4-cf68-48b5-b865-8c1951a526bf"),
                    ServerId,
                    ObservedAtUtc,
                    true,
                    18,
                    "de_dust2",
                    0,
                    20,
                    0,
                    "1.1.2.7/Stdio",
                    null),
                new(
                    Guid.Parse("73e87c47-9aef-4a95-b10c-47d8751d381e"),
                    ServerId,
                    ObservedAtUtc.AddMinutes(-30),
                    false,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    OpenIncidentReason)
            ];

            return Task.FromResult<SnapshotHistoryResponse?>(new SnapshotHistoryResponse(
                ServerId,
                null,
                null,
                limit,
                snapshots.Take(limit).ToArray()));
        }

        private static ServerResponse CreateServer() => new(
            ServerId,
            ServerName,
            "cstrike",
            "game.example.test",
            27015,
            27015,
            true,
            30,
            "Reader fixture note",
            ObservedAtUtc.AddDays(-1));

        private static AvailabilityIncidentResponse CreateOpenIncident() => new(
            OpenIncidentId,
            ServerId,
            "Unreachable",
            ObservedAtUtc.AddMinutes(-15),
            null,
            OpenIncidentReason,
            null,
            4);
    }

    private sealed class TestAuthenticationOptions : AuthenticationSchemeOptions
    {
        public string? Role { get; set; }
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<TestAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<TestAuthenticationOptions>(options, logger, encoder)
    {
        public const string SchemeName = "ReaderTest";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
                CreateClaims(Options.Role),
                SchemeName,
                ClaimTypes.Name,
                ClaimTypes.Role);
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }

        protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
        {
            Response.Redirect("/auth/forbidden");
            return Task.CompletedTask;
        }

        private static IEnumerable<Claim> CreateClaims(string? role)
        {
            yield return new Claim(ClaimTypes.Name, "Portal user");
            if (!string.IsNullOrWhiteSpace(role))
            {
                yield return new Claim(ClaimTypes.Role, role);
            }
        }
    }
}
