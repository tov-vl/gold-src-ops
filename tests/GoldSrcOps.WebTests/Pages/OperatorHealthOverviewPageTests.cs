using System.Net;
using AwesomeAssertions;
using GoldSrcOps.Web.Security;
using GoldSrcOps.Web.Services;
using GoldSrcOps.WebTests.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GoldSrcOps.WebTests.Pages;

public sealed class OperatorHealthOverviewPageTests
{
    [Theory]
    [InlineData(WebSecurity.ReaderRole)]
    [InlineData(null)]
    public async Task Denied_roles_never_read_sources_or_see_the_overview_link(string? role)
    {
        var fixture = new OperatorHealthOverviewTests.FixtureClient();
        await using var factory = new ReaderWebApplicationFactory(role);
        await using var configured = Configure(factory, fixture);
        using var client = configured.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync(OperatorHealthOverview.Path);
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        fixture.Reads.Should().Equal(0, 0, 0, 0);
        using var home = await client.GetAsync("/");
        (await home.Content.ReadAsStringAsync()).Should().NotContain("href=\"/operator/health\"");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(15)]
    public async Task Static_refresh_preserves_other_sources_links_and_no_store(int unavailableMask)
    {
        var fixture = new OperatorHealthOverviewTests.FixtureClient(unavailableMask);
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        await using var configured = Configure(factory, fixture);
        using var client = configured.CreateClient();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await client.GetAsync(OperatorHealthOverview.Path + "/");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Headers.CacheControl!.NoStore.Should().BeTrue();
            var html = await response.Content.ReadAsStringAsync();
            html.Should().Contain("Operations health").And.Contain("href=\"/operator/health\"")
                .And.Contain("Unknown and Stale never mean healthy").And.NotContain("private-sentinel").And.NotContain("Bearer ");
            html.Split("class=\"operations-card\"").Should().HaveCount(5);
            foreach (var path in new[] { "/operator/game-host-health", "/operator/archive-health", "/operator/polling-health", "/operator/credential-expiry" })
            {
                html.Should().Contain("href=\"" + path + "\"");
            }

            html.Should().Contain("data-source-state=\"" + (unavailableMask == 15 ? "Unknown" : "Available") + "\"");
            html.Length.Should().BeLessThan(65536);
        }

        fixture.Reads.Should().Equal(2, 2, 2, 2);
    }

    [Fact]
    public async Task Aborted_ssr_request_cancels_all_four_source_reads()
    {
        var fixture = new OperatorHealthOverviewTests.FixtureClient(wait: true);
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        await using var configured = Configure(factory, fixture);
        using var client = configured.CreateClient();
        using var cancellation = new CancellationTokenSource();
        var request = client.GetAsync(OperatorHealthOverview.Path, cancellation.Token);
        try
        {
            await fixture.AllStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellation.Token);
        }
        finally
        {
            await cancellation.CancelAsync();
        }

        var read = async () => await request;
        await read.Should().ThrowAsync<OperationCanceledException>();
        // The request token is already cancelled; observe propagation with an independent deadline.
        await fixture.AllCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        fixture.Reads.Should().Equal(1, 1, 1, 1);
    }

    private static WebApplicationFactory<Program> Configure(ReaderWebApplicationFactory factory, OperatorHealthOverviewTests.FixtureClient fixture) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IGameHostHealthClient>();
            services.AddSingleton<IGameHostHealthClient>(fixture);
            services.RemoveAll<IArchiveHealthClient>();
            services.AddSingleton<IArchiveHealthClient>(fixture);
            services.RemoveAll<IPollingHealthClient>();
            services.AddSingleton<IPollingHealthClient>(fixture);
            services.RemoveAll<ICredentialExpiryClient>();
            services.AddSingleton<ICredentialExpiryClient>(fixture);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new OperatorHealthOverviewFixtures.Clock());
        }));
}
