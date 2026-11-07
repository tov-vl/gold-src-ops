using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using GoldSrcOps.Contracts.Monitoring;

namespace GoldSrcOps.UnitTests.Api;

public sealed class GameHostHealthEndpointTests
{
    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("reader", HttpStatusCode.Forbidden)]
    [InlineData("no-role", HttpStatusCode.Forbidden)]
    [InlineData("no-subject", HttpStatusCode.Unauthorized)]
    [InlineData("operator", HttpStatusCode.OK)]
    public async Task Only_operator_with_stable_subject_can_read(string identity, HttpStatusCode expected)
    {
        var principal = identity switch
        {
            "anonymous" => TestApiPrincipal.Anonymous,
            "reader" => TestApiPrincipal.Reader(),
            "no-role" => TestApiPrincipal.WithoutRoles(),
            "no-subject" => TestApiPrincipal.WithoutSubject(),
            _ => TestApiPrincipal.Operator()
        };
        await using var factory = new GoldSrcOpsApiFactory(principal: principal);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/operator/game-host-health");
        response.StatusCode.Should().Be(expected);
        if (expected == HttpStatusCode.OK)
        {
            response.Headers.CacheControl!.NoStore.Should().BeTrue();
            var snapshot = await response.Content.ReadFromJsonAsync<GameHostHealthResponse>();
            snapshot!.Components.Should().HaveCount(5).And.OnlyContain(static item => item.State == "Unknown");
            snapshot.Warnings!.State.Should().Be("Unknown");
            snapshot.Warnings.Items.Should().BeEmpty();
        }
    }
}
