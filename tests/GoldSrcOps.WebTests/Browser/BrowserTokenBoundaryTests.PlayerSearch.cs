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
            foreach (var match in new[] { "exact", "contains" })
                foreach (var language in new[] { "ru", "en" })
                {
                    foreach (var width in new[] { 1440, 390, 320 })
                    {
                        var name = "SearchPlayer" + new string('W', 19);
                        var query = string.Equals(match, "contains", StringComparison.Ordinal) ? "searchplayer" : name;
                        factory.SearchHandler.Result = new("fresh", DateTimeOffset.UtcNow, true,
                            Enumerable.Range(1, 10).Select(i => new PublicPlayerSearchEntryResponse(i, name, 1, 99, 8)).ToArray(), null);
                        await Page.SetViewportSizeAsync(width, 900);
                        await Page.GotoAsync(new Uri(factory.ClientOptions.BaseAddress, "/leaderboard?lang=" + language).AbsoluteUri);
                        var calls = factory.SearchHandler.RequestCount;
                        await Page.Locator("#player-search-match").SelectOptionAsync(match);
                        await Page.Locator("#player-search-name").FillAsync(query);
                        factory.SearchHandler.RequestCount.Should().Be(calls);
                        await Page.Locator("#player-search-name").PressAsync("Enter");
                        await Expect(Page.Locator(".player-search-result")).ToHaveCountAsync(10);
                        await Expect(Page.Locator(".player-search-progress").First).ToContainTextAsync("1");
                        await Expect(Page.Locator(".player-search-notice")).ToContainTextAsync("10");
                        Page.Url.Should().NotContain(name).And.NotContain(query);
                        factory.SearchHandler.LastMatch.Should().Be(match);
                        await Expect(Page.Locator("#player-search-match")).ToHaveValueAsync(match);
                        if (width == 320 && string.Equals(match, "contains", StringComparison.Ordinal))
                            await Page.AddStyleTagAsync(new() { Content = "html { font-size: 200%; }" });
                        factory.SearchHandler.RequestCount.Should().Be(calls + 1);
                        var layout = await Page.EvaluateAsync<string>("""
                        JSON.stringify({ width: document.documentElement.clientWidth,
                            scrollWidth: document.documentElement.scrollWidth, ready: document.readyState,
                            overflow: [...document.querySelectorAll('body *')]
                                .filter(element => element.getBoundingClientRect().right > document.documentElement.clientWidth)
                                .map(element => `${element.tagName}.${element.className}: ${element.getBoundingClientRect().right}`) })
                        """);
                        using (var geometry = System.Text.Json.JsonDocument.Parse(layout))
                        {
                            geometry.RootElement.GetProperty("scrollWidth").GetInt32().Should()
                                .BeLessThanOrEqualTo(geometry.RootElement.GetProperty("width").GetInt32(), $"{language} at {width}px: {layout}");
                        }
                        await Page.Locator("[data-leaderboard-refresh]").ClickAsync();
                        await Expect(Page.Locator("#leaderboard-refresh-status")).ToHaveTextAsync(string.Equals(language, "en", StringComparison.Ordinal)
                            ? "Check complete. Fresh snapshot received." : "Проверка завершена. Свежий снимок получен.");
                        await Expect(Page.Locator(".player-search-result")).ToHaveCountAsync(10);
                        await Expect(Page.Locator("#player-search-name")).ToHaveValueAsync(query);
                        factory.SearchHandler.RequestCount.Should().Be(calls + 1);
                        await Expect(Page.Locator("#player-search-match")).ToHaveValueAsync(match);
                        await Page.Locator("#player-search-name").FocusAsync();
                        await Page.Locator("#player-search").ScrollIntoViewIfNeededAsync();
                        await CaptureScreenshotIfRequestedAsync("player-search-" + match + "-" + language, new ViewportSize { Width = width, Height = 900 });
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
        await Page.Locator("#player-search-submit").ClickAsync();
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
    public async Task Player_search_countdown_blocks_early_submit_and_enables_only_manual_retry_in_both_languages()
    {
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory(searchEnabled: true);
        factory.StartServer();
        var now = DateTime.UtcNow;
        await Page.Clock.InstallAsync(new() { TimeDate = now });
        await Page.Clock.PauseAtAsync(now.AddSeconds(1));
        await Page.SetViewportSizeAsync(320, 740);
        foreach (var language in new[] { "ru", "en" })
        {
            var english = string.Equals(language, "en", StringComparison.Ordinal);
            factory.SearchHandler.Status = HttpStatusCode.TooManyRequests;
            factory.SearchHandler.Result = new("busy", null, false, [], 10);
            await Page.GotoAsync(new Uri(factory.ClientOptions.BaseAddress, "/leaderboard?lang=" + language).AbsoluteUri);
            var calls = factory.SearchHandler.RequestCount;
            var input = Page.Locator("#player-search-name");
            var button = Page.Locator("#player-search-submit");
            var message = Page.Locator("#player-search-message");
            await input.FillAsync("Example");
            await input.PressAsync("Enter");
            await Expect(button).ToBeDisabledAsync();
            await Expect(input).ToHaveValueAsync("Example");
            await Expect(message).ToHaveTextAsync(english
                ? "Search is busy. Try again manually in 10 seconds." : "Поиск занят. Повторите вручную через 10 сек.");
            await Expect(message).ToHaveAttributeAsync("aria-live", "off");
            await Expect(Page.Locator("[data-search-announcement]")).ToBeEmptyAsync();
            await Page.Locator("#player-search-match").SelectOptionAsync("contains");
            await Expect(button).ToBeDisabledAsync();
            await input.FillAsync("ChangedName");
            await input.PressAsync("Enter");
            // Also cover submit events that bypass the disabled button.
            (await Page.Locator(".player-search form").EvaluateAsync<bool>(
                "form => form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))")).Should().BeFalse();
            factory.SearchHandler.RequestCount.Should().Be(calls + 1);
            await Page.Clock.FastForwardAsync(3_000);
            await Expect(message).ToHaveTextAsync(english
                ? "Search is busy. Try again manually in 7 seconds." : "Поиск занят. Повторите вручную через 7 сек.");
            await Page.Locator("[data-leaderboard-refresh]").ClickAsync();
            await Expect(Page.Locator("#leaderboard-refresh-status")).ToHaveTextAsync(english
                ? "Check complete. Fresh snapshot received." : "Проверка завершена. Свежий снимок получен.");
            await Expect(button).ToBeDisabledAsync();
            await Expect(input).ToHaveValueAsync("ChangedName");
            (await Page.EvaluateAsync<bool>("document.documentElement.scrollWidth > document.documentElement.clientWidth")).Should().BeFalse();
            await Page.Locator("#player-search").ScrollIntoViewIfNeededAsync();
            await CaptureScreenshotIfRequestedAsync("player-search-countdown-" + language, new() { Width = 320, Height = 740 });
            await Page.Clock.FastForwardAsync(7_000);
            await Expect(button).ToBeEnabledAsync();
            await Expect(message).ToHaveTextAsync(english
                ? "You can search again. Select \"Search\" or press Enter." : "Можно повторить поиск. Нажмите \"Найти\" или Enter.");
            await Expect(Page.Locator("[data-search-announcement]")).ToHaveTextAsync(await message.InnerTextAsync());
            await Expect(input).ToHaveValueAsync("ChangedName");
            factory.SearchHandler.RequestCount.Should().Be(calls + 1);
            factory.SearchHandler.Status = HttpStatusCode.OK;
            factory.SearchHandler.Result = null;
            await input.FocusAsync();
            await input.PressAsync("Tab");
            await Expect(button).ToBeFocusedAsync();
            await button.PressAsync("Enter");
            await Expect(Page.Locator(".player-search-result")).ToHaveCountAsync(2);
            factory.SearchHandler.LastName.Should().Be("ChangedName");
            factory.SearchHandler.LastMatch.Should().Be("contains");
            await Expect(Page.Locator("#player-search-match")).ToHaveValueAsync("contains");
            factory.SearchHandler.RequestCount.Should().Be(calls + 2);
            Page.Url.Should().NotContain("ChangedName");
            await AssertBrowserStorageIsEmptyAsync();
        }
    }

    [BrowserFact]
    public async Task Player_search_countdown_catches_up_after_page_restore_without_restarting_or_submitting()
    {
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory(searchEnabled: true);
        factory.StartServer();
        factory.SearchHandler.Status = HttpStatusCode.TooManyRequests;
        factory.SearchHandler.Result = new("busy", null, false, [], 3);
        var now = DateTime.UtcNow;
        await Page.Clock.InstallAsync(new() { TimeDate = now });
        await Page.Clock.PauseAtAsync(now.AddSeconds(1));
        await Page.GotoAsync(new Uri(factory.ClientOptions.BaseAddress, "/leaderboard").AbsoluteUri);
        await Page.Locator("#player-search-name").FillAsync("Example");
        await Page.Locator("#player-search-submit").ClickAsync();
        await Expect(Page.Locator("#player-search-submit")).ToBeDisabledAsync();
        await Page.EvaluateAsync("window.dispatchEvent(new PageTransitionEvent('pagehide', { persisted: true }))");
        await Page.Clock.FastForwardAsync(20_000);
        await Page.EvaluateAsync("window.dispatchEvent(new PageTransitionEvent('pageshow', { persisted: true }))");
        await Expect(Page.Locator("#player-search-submit")).ToBeEnabledAsync();
        await Expect(Page.Locator("#player-search-name")).ToHaveValueAsync("Example");
        factory.SearchHandler.RequestCount.Should().Be(1);
    }

    [BrowserFact]
    public async Task Player_search_works_without_JavaScript_and_replaces_results_with_empty_busy_or_unavailable()
    {
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory(searchEnabled: true);
        factory.StartServer();
        await using var context = await Browser.NewContextAsync(new() { JavaScriptEnabled = false, ViewportSize = new() { Width = 320, Height = 740 } });
        var page = await context.NewPageAsync();
        foreach (var match in new[] { "exact", "contains" })
            foreach (var language in new[] { "ru", "en" })
            {
                factory.SearchHandler.Status = HttpStatusCode.OK;
                factory.SearchHandler.Result = null;
                await page.GotoAsync(new Uri(factory.ClientOptions.BaseAddress, "/leaderboard?lang=" + language).AbsoluteUri);
                await page.Locator("#player-search-match").SelectOptionAsync(match);
                await page.Locator("#player-search-name").FillAsync("Example");
                await page.Locator("#player-search-submit").ClickAsync();
                await Expect(page.Locator(".player-search-result")).ToHaveCountAsync(2);
                await Expect(page.Locator("#player-search-match")).ToHaveValueAsync(match);
                factory.SearchHandler.LastMatch.Should().Be(match);
                foreach (var state in new[] { "fresh", "busy", "unavailable" })
                {
                    factory.SearchHandler.Status = state switch { "busy" => HttpStatusCode.TooManyRequests, "unavailable" => HttpStatusCode.ServiceUnavailable, _ => HttpStatusCode.OK };
                    factory.SearchHandler.Result = new(state, string.Equals(state, "fresh", StringComparison.Ordinal) ? DateTimeOffset.UtcNow : null, false, [], string.Equals(state, "busy", StringComparison.Ordinal) ? 3 : null);
                    await page.Locator("#player-search-name").FillAsync("Nobody");
                    await page.Locator("#player-search-submit").ClickAsync();
                    await Expect(page.Locator("[data-search-state]")).ToHaveAttributeAsync("data-search-state", state);
                    await Expect(page.Locator("#player-search-submit")).ToBeEnabledAsync();
                    await Expect(page.Locator("#player-search-name")).ToHaveValueAsync("Nobody");
                    await Expect(page.Locator(".player-search-result")).ToHaveCountAsync(0);
                    await Expect(page.Locator("#player-search-match")).ToHaveValueAsync(match);
                    factory.SearchHandler.LastMatch.Should().Be(match);
                    page.Url.Should().NotContain("Nobody").And.NotContain("Example");
                }
            }
    }
}
