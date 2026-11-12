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

public sealed class RetentionHealthPageTests
{
    [Theory]
    [InlineData(WebSecurity.ReaderRole)]
    [InlineData(null)]
    public async Task Reader_and_anonymous_cannot_read_retention_or_see_its_navigation(string? role)
    {
        var fixture = new FixtureClient("unavailable");
        await using var factory = new ReaderWebApplicationFactory(role);
        await using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IRetentionHealthClient>();
            services.AddSingleton<IRetentionHealthClient>(fixture);
        }));
        using var client = configured.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync("/operator/retention-health");
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        fixture.Reads.Should().Be(0);
        using var home = await client.GetAsync("/");
        (await home.Content.ReadAsStringAsync()).Should().NotContain("href=\"/operator/retention-health\"");
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
            services.RemoveAll<IRetentionHealthClient>();
            services.AddSingleton<IRetentionHealthClient>(fixture);
        }));
        using var client = configured.CreateClient();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await client.GetAsync("/operator/retention-health/");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Headers.CacheControl!.NoStore.Should().BeTrue();
            var html = await response.Content.ReadAsStringAsync();
            html.Should().Contain("Retention health").And.Contain("href=\"/operator/retention-health\"")
                .And.Contain("Completed empty and full-batch passes").And.NotContain("private-sentinel").And.NotContain("Bearer ");
            html.Split("class=\"retention-warning\"").Should().HaveCount(4);
            html.Should().Contain(scenario switch { "valid" => "data-rule-state=\"Firing\"", "stale" => "data-observation-state=\"Stale\"", _ => "data-observation-state=\"Unknown\"" });
        }

        fixture.Reads.Should().Be(2);
    }

    private sealed class FixtureClient(string scenario) : IRetentionHealthClient
    {
        public int Reads { get; private set; }
        public Task<RetentionHealthResponse> ReadAsync(CancellationToken cancellationToken = default)
        {
            Reads++;
            if (scenario is "unavailable")
            {
                throw new HttpRequestException("private-sentinel");
            }

            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new RetentionHealthResponse(now, scenario is "foreign" ? "private-sentinel" : "Running",
                now.AddSeconds(scenario is "stale" ? -121 : 0), now.AddSeconds(-6), now.AddSeconds(-5), 5, 370, "Available", now,
                [.. RetentionHealthView.Definitions.Select(static item => new RetentionWarningResponse(item.Name, item.Name is "SnapshotRetentionCycleFailed" ? "Firing" : "Inactive"))], LastCycleDurationSeconds: 1, ConsecutiveFailures: 0, InProgress: false));
        }
    }
}
