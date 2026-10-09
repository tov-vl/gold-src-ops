using System.Net;
using AwesomeAssertions;
using GoldSrcOps.Contracts.Monitoring;

namespace GoldSrcOps.WebTests.Pages;

public sealed class PublicLeaderboardIntegrationTests
{
    [Fact]
    public async Task Leaderboard_is_anonymous_encodes_names_and_preserves_positions()
    {
        await using var factory = new PublicDashboardWebApplicationFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/leaderboard");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.Handler.RequestedPaths.Should().Equal("/api/public/leaderboard");
        body.Should().Contain("&lt;script&gt;").And.NotContain("<script>alert(1)</script>");
        WebUtility.HtmlDecode(body).Should().Contain("Свежий снимок").And.Contain("Легенда").And.Contain("Другой игрок");
        body.Should().Contain("href=\"/play\"").And.NotContain(PublicDashboardWebApplicationFactory.PrivateDataSentinel);
    }

    [Theory]
    [InlineData("fresh", "Свежий снимок", true)]
    [InlineData("stale", "Устаревший снимок", true)]
    [InlineData("unavailable", "Рейтинг временно недоступен", false)]
    [InlineData("unexpected", "Рейтинг временно недоступен", false)]
    public async Task Leaderboard_states_are_explicit_and_unavailable_never_renders_old_rows(string state, string label, bool rows)
    {
        await using var factory = new PublicDashboardWebApplicationFactory(leaderboard: new(state, DateTimeOffset.UtcNow,
            [new(1, "visible-only-if-valid-state", 0, 1, 0)]));
        using var client = factory.CreateClient();
        var body = WebUtility.HtmlDecode(await client.GetStringAsync("/leaderboard"));
        body.Should().Contain(label);
        body.Contains("visible-only-if-valid-state", StringComparison.Ordinal).Should().Be(rows);
        if (string.Equals(state, "stale", StringComparison.Ordinal))
        {
            body.Should().Contain("результаты в игре могли измениться");
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Missing_or_failed_api_retains_public_join_link(HttpStatusCode status)
    {
        await using var factory = new PublicDashboardWebApplicationFactory(leaderboardFailure: status);
        using var client = factory.CreateClient();
        var body = WebUtility.HtmlDecode(await client.GetStringAsync("/leaderboard"));
        body.Should().Contain("Рейтинг временно недоступен").And.Contain("href=\"/play\"");
        body.Should().NotContain("leaderboard-table");
    }

    [Fact]
    public async Task Fresh_empty_snapshot_is_distinct_from_missing_data()
    {
        await using var factory = new PublicDashboardWebApplicationFactory(leaderboard: new("fresh", DateTimeOffset.UtcNow, []));
        using var client = factory.CreateClient();
        var body = WebUtility.HtmlDecode(await client.GetStringAsync("/leaderboard"));
        body.Should().Contain("В рейтинге пока нет результатов.").And.Contain("Свежий снимок");
    }
}
