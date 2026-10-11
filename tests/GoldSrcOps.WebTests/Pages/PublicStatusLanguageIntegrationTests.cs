using System.Net;
using AwesomeAssertions;

namespace GoldSrcOps.WebTests.Pages;

public sealed class PublicStatusLanguageIntegrationTests
{
    [Theory]
    [InlineData("", "ru", "24h", "Статус GoldSrcOps")]
    [InlineData("lang=en&range=7d", "en", "7d", "GoldSrcOps status")]
    [InlineData("lang=EN&range=24h", "en", "24h", "GoldSrcOps status")]
    [InlineData("lang=unknown&range=invalid", "ru", "24h", "Статус GoldSrcOps")]
    [InlineData("lang=en&lang=ru&range=7d", "en", "7d", "GoldSrcOps status")]
    public async Task Status_language_and_history_window_are_bounded_and_preserved(
        string query, string language, string window, string title)
    {
        await using var factory = new PublicDashboardWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US");
        using var response = await client.GetAsync("/?" + query);
        var body = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        var english = string.Equals(language, "en", StringComparison.Ordinal);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Should().NotContain(header => header.Key == "Set-Cookie");
        body.Should().Contain($"<title>{title}</title>")
            .And.Contain($"class=\"site-shell\" lang=\"{language}\"")
            .And.Contain($"class=\"status-page\" lang=\"{language}\"")
            .And.Contain($"href=\"/?range={window}&lang=en\"")
            .And.Contain($"href=\"/?range={window}\"")
            .And.Contain("connect play.example.test:27015")
            .And.Contain("99.5%").And.Contain("UTC")
            .And.NotContain(PublicDashboardWebApplicationFactory.PrivateDataSentinel)
            .And.NotContain("Text[").And.NotContain("invalid").And.NotContain("lang=unknown");
        var suffix = english ? "?lang=en" : string.Empty;
        foreach (var path in new[] { "/", "/play", "/leaderboard" })
        {
            body.Should().Contain($"href=\"{path}{suffix}\"");
        }
        body.Should().Contain(english ? "Bucket starting" : "Интервал с")
            .And.Contain(english ? "Connection command copied." : "Команда подключения скопирована.");
        if (english) body.Should().NotMatchRegex("[А-Яа-я]");
        factory.Handler.LastHistoryWindow.Should().Be(window);
        factory.Handler.RequestedPaths.Should().BeEquivalentTo(
            "/api/public/status", "/api/public/a2s-history", "/api/public/server");
    }

    [Theory]
    [InlineData("ru", "Не удалось получить данные мониторинга", "История доступности по A2S")]
    [InlineData("en", "Monitoring data could not be reached", "A2S reachability history")]
    public async Task Unavailable_status_keeps_translated_history_and_navigation(
        string language, string failure, string history)
    {
        await using var factory = new PublicDashboardWebApplicationFactory(statusUnavailable: true, serverJoinUnavailable: true);
        using var client = factory.CreateClient();
        var body = WebUtility.HtmlDecode(await client.GetStringAsync("/?lang=" + language));
        body.Should().Contain(failure).And.Contain(history).And.NotContain("data-copy-connect");
        if (string.Equals(language, "en", StringComparison.Ordinal)) body.Should().NotMatchRegex("[А-Яа-я]");
    }
}
