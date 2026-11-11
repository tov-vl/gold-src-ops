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

public sealed class ArchiveHealthPageTests
{
    [Theory]
    [InlineData(WebSecurity.ReaderRole)]
    [InlineData(null)]
    public async Task Reader_and_anonymous_cannot_read_archive_or_see_its_navigation(string? role)
    {
        var fixture = new FixtureClient("unavailable");
        await using var factory = new ReaderWebApplicationFactory(role);
        await using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IArchiveHealthClient>();
            services.AddSingleton<IArchiveHealthClient>(fixture);
        }));
        using var client = configured.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync("/operator/archive-health");
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        fixture.Reads.Should().Be(0);
        using var home = await client.GetAsync("/");
        (await home.Content.ReadAsStringAsync()).Should().NotContain("href=\"/operator/archive-health\"");
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("unavailable")]
    [InlineData("stale")]
    [InlineData("foreign")]
    public async Task Operator_refresh_is_static_bounded_and_projects_only_fixed_guidance(string scenario)
    {
        var fixture = new FixtureClient(scenario);
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        await using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IArchiveHealthClient>();
            services.AddSingleton<IArchiveHealthClient>(fixture);
        }));
        using var client = configured.CreateClient();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await client.GetAsync("/operator/archive-health/");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Headers.CacheControl!.NoStore.Should().BeTrue();
            var html = await response.Content.ReadAsStringAsync();
            html.Should().Contain("Archive health").And.Contain("href=\"/operator/archive-health\"")
                .And.Contain("Manual runs and reruns do not refresh").And.NotContain("private-sentinel").And.NotContain("Bearer ");
            html.Split("class=\"archive-warning\"").Should().HaveCount(5);
            html.Should().Contain(scenario switch { "valid" => "data-rule-state=\"Firing\"", "stale" => "data-observation-state=\"Stale\"", _ => "data-observation-state=\"Unknown\"" });
        }

        fixture.Reads.Should().Be(2);
    }

    private sealed class FixtureClient(string scenario) : IArchiveHealthClient
    {
        public int Reads { get; private set; }
        public Task<ArchiveHealthResponse> ReadAsync(CancellationToken cancellationToken = default)
        {
            Reads++;
            if (scenario is "unavailable")
            {
                throw new HttpRequestException("private-sentinel");
            }

            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new ArchiveHealthResponse(now, scenario is "foreign" ? "private-sentinel" : "Available",
                now.AddSeconds(scenario is "stale" ? -1801 : 0), HasScheduledSuccess: true, now.AddHours(-4), 14400, "Available", now,
                [.. ArchiveHealthView.Definitions.Select(static item => new ArchiveWarningResponse(item.Name, item.Name is "ArchiveScheduleDelayed" ? "Firing" : "Inactive"))]));
        }
    }
}
