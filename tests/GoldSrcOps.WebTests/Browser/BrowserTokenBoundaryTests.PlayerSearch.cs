using System.Net;
using AwesomeAssertions;
using GoldSrcOps.Contracts.Monitoring;
using Microsoft.Playwright;

namespace GoldSrcOps.WebTests.Browser;

public sealed partial class BrowserTokenBoundaryTests
{
    [BrowserFact]
    public async Task Player_search_preserves_duplicates_and_progress_without_query_URL_or_refresh_replay()
    {
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory(searchEnabled: true);
        factory.StartServer();
        var browserApiRequests = 0;
        void ObserveRequest(object? sender, IRequest request)
        {
            if (request.Url.Contains("/api/", StringComparison.Ordinal))
                browserApiRequests++;
            request.Url.Should().NotContain("SearchPlayer");
        }
        Page.Request += ObserveRequest;
        try
        {
            foreach (var language in new[] { "ru", "en" })
            {
                foreach (var width in new[] { 1440, 390, 320 })
                {
                    var name = "SearchPlayer" + new string('W', 19);
                    factory.SearchHandler.Result = new("fresh", DateTimeOffset.UtcNow, true,
                        Enumerable.Range(1, 10).Select(i => new PublicPlayerSearchEntryResponse(i, name, 1, 99, 8)).ToArray(), null);
                    await Page.SetViewportSizeAsync(width, 900);
                    await Page.GotoAsync(new Uri(factory.ClientOptions.BaseAddress, "/leaderboard?lang=" + language).AbsoluteUri);
                    var calls = factory.SearchHandler.RequestCount;
                    await Page.Locator("#player-search-name").FillAsync(name);
                    factory.SearchHandler.RequestCount.Should().Be(calls);
                    await Page.Locator("#player-search-name").PressAsync("Enter");
                    await Expect(Page.Locator(".player-search-result")).ToHaveCountAsync(10);
                    await Expect(Page.Locator(".player-search-progress").First).ToContainTextAsync("1");
                    await Expect(Page.Locator(".player-search-notice")).ToContainTextAsync("10");
                    Page.Url.Should().NotContain(name);
                    factory.SearchHandler.RequestCount.Should().Be(calls + 1);
                    (await Page.EvaluateAsync<bool>("document.documentElement.scrollWidth > document.documentElement.clientWidth")).Should().BeFalse();
                    await Page.Locator("[data-leaderboard-refresh]").ClickAsync();
                    await Expect(Page.Locator("#leaderboard-refresh-status")).ToHaveTextAsync(string.Equals(language, "en", StringComparison.Ordinal)
                        ? "Check complete. Fresh snapshot received." : "Проверка завершена. Свежий снимок получен.");
                    await Expect(Page.Locator(".player-search-result")).ToHaveCountAsync(10);
                    await Expect(Page.Locator("#player-search-name")).ToHaveValueAsync(name);
                    factory.SearchHandler.RequestCount.Should().Be(calls + 1);
                    await Page.Locator("#player-search-name").FocusAsync();
                    await Page.Locator("#player-search").ScrollIntoViewIfNeededAsync();
                    await CaptureScreenshotIfRequestedAsync("player-search-" + language, new ViewportSize { Width = width, Height = 900 });
                    AssertTokenFree(await Page.ContentAsync());
                    await AssertBrowserStorageIsEmptyAsync();
                }
            }
            browserApiRequests.Should().Be(0);
        }
        finally
        {
            Page.Request -= ObserveRequest;
        }
    }

    [BrowserFact]
    public async Task Player_search_results_survive_automatic_leaderboard_refresh_without_another_lookup()
    {
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory(searchEnabled: true);
        factory.StartServer();
        await Page.GotoAsync(new Uri(factory.ClientOptions.BaseAddress, "/leaderboard").AbsoluteUri);
        await Page.Locator("#player-search-name").FillAsync("Example");
        await Page.Locator(".player-search button").ClickAsync();
        await Expect(Page.Locator(".player-search-result")).ToHaveCountAsync(2);
        var now = DateTime.UtcNow;
        await Page.Clock.InstallAsync(new() { TimeDate = now });
        await Page.Clock.PauseAtAsync(now.AddSeconds(1));
        await Page.RunAndWaitForResponseAsync(() => Page.Clock.FastForwardAsync(60_000), response => string.Equals(response.Request.Method, "GET", StringComparison.Ordinal) && response.Url.EndsWith("/leaderboard", StringComparison.Ordinal));
        await Expect(Page.Locator("#leaderboard-refresh-status")).ToHaveTextAsync("Проверка завершена. Свежий снимок получен.");
        await Expect(Page.Locator(".player-search-result")).ToHaveCountAsync(2);
        factory.SearchHandler.RequestCount.Should().Be(1);
    }

    [BrowserFact]
    public async Task Player_search_works_without_JavaScript_and_replaces_results_with_empty_busy_or_unavailable()
    {
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory(searchEnabled: true);
        factory.StartServer();
        await using var context = await Browser.NewContextAsync(new() { JavaScriptEnabled = false, ViewportSize = new() { Width = 320, Height = 740 } });
        var page = await context.NewPageAsync();
        foreach (var language in new[] { "ru", "en" })
        {
            factory.SearchHandler.Status = HttpStatusCode.OK;
            factory.SearchHandler.Result = null;
            await page.GotoAsync(new Uri(factory.ClientOptions.BaseAddress, "/leaderboard?lang=" + language).AbsoluteUri);
            await page.Locator("#player-search-name").FillAsync("Example");
            await page.Locator(".player-search button").ClickAsync();
            await Expect(page.Locator(".player-search-result")).ToHaveCountAsync(2);
            foreach (var state in new[] { "fresh", "busy", "unavailable" })
            {
                factory.SearchHandler.Status = state switch { "busy" => HttpStatusCode.TooManyRequests, "unavailable" => HttpStatusCode.ServiceUnavailable, _ => HttpStatusCode.OK };
                factory.SearchHandler.Result = new(state, string.Equals(state, "fresh", StringComparison.Ordinal) ? DateTimeOffset.UtcNow : null, false, [], string.Equals(state, "busy", StringComparison.Ordinal) ? 3 : null);
                await page.Locator("#player-search-name").FillAsync("Nobody");
                await page.Locator(".player-search button").ClickAsync();
                await Expect(page.Locator("[data-search-state]")).ToHaveAttributeAsync("data-search-state", state);
                await Expect(page.Locator(".player-search-result")).ToHaveCountAsync(0);
                page.Url.Should().NotContain("Nobody").And.NotContain("Example");
            }
        }
    }
}
