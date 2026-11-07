using System.Net;
using AwesomeAssertions;
using GoldSrcOps.Contracts.Monitoring;
using GoldSrcOps.Web.Security;
using GoldSrcOps.Web.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GoldSrcOps.WebTests.Pages;

public sealed class GameHostHealthPageTests
{
    [Theory]
    [InlineData(WebSecurity.ReaderRole)]
    [InlineData(null)]
    public async Task Reader_and_anonymous_cannot_render_health(string? role)
    {
        await using var factory = new ReaderWebApplicationFactory(role);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync("/operator/game-host-health");
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Operator_sees_sanitized_snapshot_or_explicit_unknown(bool unavailable)
    {
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        await using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IGameHostHealthClient>();
            services.AddSingleton<IGameHostHealthClient>(new FixtureClient(unavailable));
        }));
        using var client = configured.CreateClient();
        using var response = await client.GetAsync("/operator/game-host-health");
        var html = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain("Game-host health").And.Contain("does not prove delivery to IRM");
        html.Should().NotContain("private-host-sentinel").And.NotContain("Authorization");
        html.Should().Contain(unavailable ? "No usable health snapshot" : "Firing");
        if (unavailable)
        {
            html.Should().Contain("#unknown-snapshot").And.Contain("Refresh once");
        }
    }

    [Theory]
    [InlineData("Available", "warnings-empty")]
    [InlineData("Stale", "warnings-stale")]
    [InlineData("Unknown", "warnings-unknown")]
    public async Task Empty_unknown_and_stale_warning_snapshots_have_distinct_output(string state, string marker)
    {
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        await using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IGameHostHealthClient>();
            services.AddSingleton<IGameHostHealthClient>(new FixtureClient(unavailable: false,
                new GameHostWarningsResponse(state, DateTimeOffset.UtcNow.AddSeconds(-120), [])));
        }));
        using var client = configured.CreateClient();
        var html = await client.GetStringAsync("/operator/game-host-health");
        html.Should().Contain(marker);
        html.Should().Contain(GameHostWarningGuidance.RunbookHome);
        if (!string.Equals(state, "Available", StringComparison.Ordinal))
        {
            html.Should().NotContain("warnings-empty");
            html.Should().Contain(string.Equals(state, "Stale", StringComparison.Ordinal) ? "#stale-snapshot" : "#unknown-snapshot");
        }
    }

    [Theory]
    [InlineData("PlayerBackupInterventionRequired", "#backup-intervention-required", true)]
    [InlineData("<script>private-sentinel</script>", "#backup-intervention-required", false)]
    public async Task Warning_guidance_is_static_and_provider_text_is_encoded(string name, string anchor, bool known)
    {
        var warning = new GameHostWarningResponse(name, "player-backup", "Firing", "<img src=x onerror=private-sentinel>");
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        await using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IGameHostHealthClient>();
            services.AddSingleton<IGameHostHealthClient>(new FixtureClient(unavailable: false,
                new GameHostWarningsResponse("Available", DateTimeOffset.UtcNow, [warning])));
        }));
        using var client = configured.CreateClient();
        var html = await client.GetStringAsync("/operator/game-host-health");
        html.Should().Contain("&lt;img").And.NotContain("<img src=x").And.NotContain("<script>private-sentinel");
        if (known)
        {
            html.Should().Contain(GameHostWarningGuidance.RunbookHome + anchor)
                .And.Contain("Do not retry an uncertain backup").And.Contain("First check:").And.Contain("Preserve:");
        }
        else
        {
            html.Should().NotContain("class=\"warning-runbook\"");
        }
    }

    private sealed class FixtureClient(bool unavailable, GameHostWarningsResponse? warnings = null) : IGameHostHealthClient
    {
        public Task<GameHostHealthResponse> ReadAsync(CancellationToken cancellationToken = default) => unavailable
            ? throw new HttpRequestException("private-host-sentinel")
            : Task.FromResult(new GameHostHealthResponse(DateTimeOffset.UtcNow,
                [new GameHostHealthComponentResponse("backup", "Firing", DateTimeOffset.UtcNow)],
                [new GameHostHealthValueResponse("swapUsedBytes", string.Empty, 0)], warnings));
    }
}
