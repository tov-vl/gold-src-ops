using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using GoldSrcOps.Application.Common;
using GoldSrcOps.Application.Monitoring;
using GoldSrcOps.Contracts.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace GoldSrcOps.UnitTests.Api;

public sealed class CredentialExpiryEndpointTests
{
    [Fact]
    public async Task Configured_dates_reach_the_endpoint_without_raw_values_or_unknown_keys()
    {
        var clock = new Mock<IClock>();
        clock.SetupGet(static item => item.UtcNow).Returns(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));
        await using var factory = new GoldSrcOpsApiFactory(configureTestServices: services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton(clock.Object);
        }, configurationOverrides: new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["CredentialExpiry:GrafanaMetrics:ExpiresAtUtc"] = "2026-11-08T12:00:00Z",
            ["CredentialExpiry:GrafanaMetrics:VerifiedAtUtc"] = "2026-11-08T11:00:00Z",
            ["CredentialExpiry:GrafanaLogs:ExpiresAtUtc"] = "private-sentinel",
            ["CredentialExpiry:ArchiveReader:ExpiresAtMinuteUtc"] = "2026-11-15T12:00Z",
            ["CredentialExpiry:ArchiveReader:VerifiedAtUtc"] = "2026-11-08T11:00:00Z",
            ["CredentialExpiry:private-sentinel:ExpiresAtUtc"] = "2027-01-01T00:00:00Z"
        });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/operator/credential-expiry");
        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain("private-sentinel");
        var result = await response.Content.ReadFromJsonAsync<CredentialExpiryResponse>();
        result!.Entries[0].State.Should().Be("Expired");
        result.Entries[0].RemainingDays.Should().Be(0);
        result.Entries[1].State.Should().Be("Unknown");
        result.Entries[2].State.Should().Be("Critical");
        result.Entries[2].ExpiresAtUtc.Should().BeNull();
        result.Entries[2].ExpiresAtMinuteUtc.Should().Be("2026-11-15T12:00Z");
        result.Entries[3].State.Should().Be("Unknown");
    }

    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("reader", HttpStatusCode.Forbidden)]
    [InlineData("no-role", HttpStatusCode.Forbidden)]
    [InlineData("no-subject", HttpStatusCode.Unauthorized)]
    [InlineData("operator", HttpStatusCode.OK)]
    public async Task Only_operator_can_read_date_metadata(string identity, HttpStatusCode expected)
    {
        var principal = identity switch
        {
            "anonymous" => TestApiPrincipal.Anonymous,
            "reader" => TestApiPrincipal.Reader(),
            "no-role" => TestApiPrincipal.WithoutRoles(),
            "no-subject" => TestApiPrincipal.WithoutSubject(),
            _ => TestApiPrincipal.Operator()
        };
        var source = new Mock<ICredentialExpirySource>(MockBehavior.Strict);
        if (expected == HttpStatusCode.OK)
        {
            source.Setup(static item => item.Read()).Returns([]);
        }

        await using var factory = new GoldSrcOpsApiFactory(principal: principal, configureTestServices: services =>
        {
            services.RemoveAll<ICredentialExpirySource>();
            services.AddSingleton(source.Object);
        });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/operator/credential-expiry");
        response.StatusCode.Should().Be(expected);
        if (expected == HttpStatusCode.OK)
        {
            response.Headers.CacheControl!.NoStore.Should().BeTrue();
            var result = await response.Content.ReadFromJsonAsync<CredentialExpiryResponse>();
            result!.Entries.Should().HaveCount(4).And.OnlyContain(static item => item.State == "Unknown");
            source.Verify(static item => item.Read(), Times.Once);
        }

        source.VerifyNoOtherCalls();
    }
}
