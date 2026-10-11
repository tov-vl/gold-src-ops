using System.Net;
using AwesomeAssertions;

namespace GoldSrcOps.WebTests.Pages;

public sealed class PublicLanguageIntegrationTests
{
    [Theory]
    [InlineData("en", "en", "Server is responding", "Legend")]
    [InlineData("EN", "en", "Server is responding", "Legend")]
    [InlineData("ru", "ru", "Сервер отвечает", "Легенда")]
    [InlineData("de", "ru", "Сервер отвечает", "Легенда")]
    [InlineData("en&lang=ru", "en", "Server is responding", "Legend")]
    public async Task Language_is_bounded_and_preserved_in_public_navigation(string query, string language, string status, string rank)
    {
        await using var factory = new PublicDashboardWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US");
        var play = WebUtility.HtmlDecode(await client.GetStringAsync($"/play?lang={query}"));
        var leaderboard = WebUtility.HtmlDecode(await client.GetStringAsync($"/leaderboard?lang={query}"));
        var suffix = string.Equals(language, "en", StringComparison.Ordinal) ? "?lang=en" : string.Empty;

        play.Should().Contain($"class=\"play-page\" lang=\"{language}\"").And.Contain(status);
        leaderboard.Should().Contain($"class=\"leaderboard-page\" lang=\"{language}\"").And.Contain(rank);
        foreach (var body in new[] { play, leaderboard })
        {
            body.Should().Contain($"href=\"/play{suffix}\"").And.Contain($"href=\"/leaderboard{suffix}\"");
            body.Should().NotContain(PublicDashboardWebApplicationFactory.PrivateDataSentinel);
            body.Should().NotContain("Text[", "raw resource expressions must never become visible content");
        }
        factory.Handler.RequestedPaths.Should().Equal("/api/public/server", "/api/public/leaderboard");
        play.Should().Contain("steam://connect/play.example.test:27015");
        leaderboard.Should().Contain("Игрок <script>alert(1)</script>", "player names are not translated");
    }

    [Fact]
    public async Task English_errors_and_empty_results_remain_english_without_setting_cookies()
    {
        await using var factory = new PublicDashboardWebApplicationFactory(
            serverJoinFailureStatus: HttpStatusCode.ServiceUnavailable,
            leaderboardFailure: HttpStatusCode.ServiceUnavailable);
        using var client = factory.CreateClient();
        foreach (var path in new[] { "play", "leaderboard" })
        {
            using var response = await client.GetAsync($"/{path}?lang=en");
            var body = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
            body.Should().Contain(string.Equals(path, "play", StringComparison.Ordinal)
                ? "Connection is currently unavailable" : "Leaderboard temporarily unavailable");
            body.Should().NotMatchRegex("[А-Яа-я]", "static English text must not fall back to Russian");
            response.Headers.Should().NotContain(header => header.Key == "Set-Cookie");
        }
        await using var emptyFactory = new PublicDashboardWebApplicationFactory(leaderboard: new("fresh", DateTimeOffset.UtcNow, []));
        using var emptyClient = emptyFactory.CreateClient();
        var empty = WebUtility.HtmlDecode(await emptyClient.GetStringAsync("/leaderboard?lang=en"));
        empty.Should().Contain("No results on the leaderboard yet.").And.NotContain("leaderboard-table");
    }
}
