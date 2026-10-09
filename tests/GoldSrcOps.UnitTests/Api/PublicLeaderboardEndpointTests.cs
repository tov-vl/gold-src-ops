using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using GoldSrcOps.Application.Monitoring;
using GoldSrcOps.Contracts.Monitoring;
using Microsoft.Extensions.DependencyInjection;

namespace GoldSrcOps.UnitTests.Api;

public sealed class PublicLeaderboardEndpointTests
{
    [Fact]
    public async Task Anonymous_endpoint_reads_only_cached_safe_projection_without_database_access()
    {
        await using var factory = new GoldSrcOpsApiFactory(principal: TestApiPrincipal.Anonymous,
            configurationOverrides: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["PublicLeaderboard:Enabled"] = "true",
                ["PublicServerJoin:ServerId"] = Guid.NewGuid().ToString(),
                ["PublicServerJoin:Name"] = "Fixture",
                ["PublicServerJoin:Host"] = "play.example.test",
                ["PublicServerJoin:Port"] = "27015"
            });
        using var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<PublicLeaderboardStore>();
        var empty = await client.GetFromJsonAsync<PublicLeaderboardResponse>("/api/public/leaderboard");
        empty!.State.Should().Be("unavailable");
        var now = DateTimeOffset.UtcNow;
        store.Publish(new(now, [new(1, "Игрок", 1, 25, 3)]));
        using var response = await client.GetAsync("/api/public/leaderboard");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("Steam").And.NotContain("Secret").And.NotContain("ServerId").And.NotContain("Host");
        var result = await response.Content.ReadFromJsonAsync<PublicLeaderboardResponse>();
        result!.Entries.Should().Equal(new PublicLeaderboardEntryResponse(1, "Игрок", 1, 25, 3));
        store.RecordFailure();
        (await client.GetFromJsonAsync<PublicLeaderboardResponse>("/api/public/leaderboard"))!.State.Should().Be("stale");
        store.RecordFailure(sourceUnavailable: true);
        (await client.GetFromJsonAsync<PublicLeaderboardResponse>("/api/public/leaderboard"))!.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Disabled_endpoint_returns_not_found()
    {
        await using var factory = new GoldSrcOpsApiFactory(principal: TestApiPrincipal.Anonymous);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/public/leaderboard");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
