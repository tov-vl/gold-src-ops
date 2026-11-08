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

public sealed class CredentialExpiryPageTests
{
    [Theory]
    [InlineData(WebSecurity.ReaderRole)]
    [InlineData(null)]
    public async Task Reader_and_anonymous_are_denied_before_upstream_read(string? role)
    {
        var fixture = new FixtureClient(unavailable: true);
        await using var factory = new ReaderWebApplicationFactory(role);
        await using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICredentialExpiryClient>();
            services.AddSingleton<ICredentialExpiryClient>(fixture);
        }));
        using var client = configured.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync("/operator/credential-expiry");
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        fixture.Reads.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Operator_sees_four_fixed_purposes_and_refresh_without_provider_text(bool unavailable)
    {
        var fixture = new FixtureClient(unavailable);
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        await using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICredentialExpiryClient>();
            services.AddSingleton<ICredentialExpiryClient>(fixture);
        }));
        using var client = configured.CreateClient();
        for (var read = 0; read < 2; read++)
        {
            using var response = await client.GetAsync("/operator/credential-expiry/");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Headers.CacheControl!.NoStore.Should().BeTrue();
            var html = await response.Content.ReadAsStringAsync();
            html.Should().Contain("Credential expiry").And.Contain("Backblaze B2").And.Contain("Last verified")
                .And.Contain("href=\"/operator/credential-expiry\"").And.Contain("Unknown")
                .And.Contain("not live provider checks").And.NotContain("private-sentinel").And.NotContain("Bearer ");
            html.Split("class=\"expiry-card\"").Should().HaveCount(5);
            html.Should().Contain(unavailable ? "metadata is missing" : "Due within 7 days");
        }

        fixture.Reads.Should().Be(2);
    }

    private sealed class FixtureClient(bool unavailable) : ICredentialExpiryClient
    {
        public int Reads { get; private set; }

        public Task<CredentialExpiryResponse> ReadAsync(CancellationToken cancellationToken = default)
        {
            Reads++;
            if (unavailable)
            {
                throw new HttpRequestException("private-sentinel");
            }

            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new CredentialExpiryResponse(now, [
                new CredentialExpiryEntryResponse("grafana-metrics", "Critical", now.AddDays(7), now, 7),
                new CredentialExpiryEntryResponse("private-sentinel", "Recorded", now.AddDays(90), now, 90)
            ]));
        }
    }
}
