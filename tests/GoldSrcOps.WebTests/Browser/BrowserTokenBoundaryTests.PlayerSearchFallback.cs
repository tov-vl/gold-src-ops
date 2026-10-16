using System.Net;
using AwesomeAssertions;
using Microsoft.Playwright;

namespace GoldSrcOps.WebTests.Browser;

public sealed partial class BrowserTokenBoundaryTests
{
    [BrowserFact]
    public async Task Player_search_empty_exact_fallback_preserves_query_and_busy_retry_with_keyboard_and_without_scripts()
    {
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory(searchEnabled: true);
        factory.StartServer();
        foreach (var scripts in new[] { true, false })
        {
            await using var context = await Browser.NewContextAsync(new() { JavaScriptEnabled = scripts });
            var page = await context.NewPageAsync();
            if (scripts)
            {
                var now = DateTime.UtcNow;
                await page.Clock.InstallAsync(new() { TimeDate = now });
                await page.Clock.PauseAtAsync(now.AddSeconds(1));
            }
            foreach (var language in new[] { "ru", "en" })
            {
                factory.SearchHandler.Status = HttpStatusCode.OK;
                factory.SearchHandler.Result = new("fresh", DateTimeOffset.UtcNow, false, [], null);
                var calls = factory.SearchHandler.RequestCount;
                await page.GotoAsync(new Uri(factory.ClientOptions.BaseAddress, "/leaderboard?lang=" + language).AbsoluteUri);
                var input = page.Locator("#player-search-name");
                var submit = page.Locator("#player-search-submit");
                var fallback = page.Locator("#player-search-contains");
                await Expect(fallback).ToHaveCountAsync(0);
                await input.FillAsync("NoMatch");
                factory.SearchHandler.RequestCount.Should().Be(calls);
                await input.PressAsync("Enter");
                await Expect(fallback).ToHaveTextAsync(string.Equals(language, "en", StringComparison.Ordinal)
                    ? "Search by part of nickname" : "Искать по части ника");
                await Expect(page.Locator("#player-search-match")).ToHaveValueAsync("exact");
                factory.SearchHandler.LastMatch.Should().Be("exact");
                factory.SearchHandler.RequestCount.Should().Be(calls + 1);

                await page.WaitForLoadStateAsync(LoadState.Load);
                foreach (var width in new[] { 320, 1440 })
                {
                    await page.SetViewportSizeAsync(width, 900);
                    await page.EvaluateAsync("document.documentElement.style.fontSize = '200%'");
                    var layout = await page.EvaluateAsync<string>("""
                    JSON.stringify({ width: document.documentElement.clientWidth,
                        scrollWidth: document.documentElement.scrollWidth, ready: document.readyState,
                        overflow: [...document.querySelectorAll('body *')]
                            .filter(element => element.getBoundingClientRect().right > document.documentElement.clientWidth)
                            .map(element => `${element.tagName}.${element.className}: ${element.getBoundingClientRect().right}`) })
                    """);
                    using (var geometry = System.Text.Json.JsonDocument.Parse(layout))
                    {
                        geometry.RootElement.GetProperty("scrollWidth").GetInt32().Should()
                            .BeLessThanOrEqualTo(geometry.RootElement.GetProperty("width").GetInt32(), $"{language}, scripts={scripts}, width={width}: {layout}");
                    }
                    if (scripts && Environment.GetEnvironmentVariable(ScreenshotDirectoryVariable) is { Length: > 0 } directory)
                    {
                        await fallback.FocusAsync();
                        Directory.CreateDirectory(directory);
                        await page.Locator("#player-search").ScreenshotAsync(new() { Path = Path.Combine(directory, $"player-search-empty-{language}-{width}.png") });
                    }
                    await page.EvaluateAsync("document.documentElement.style.fontSize = ''");
                }

                factory.SearchHandler.Status = HttpStatusCode.TooManyRequests;
                factory.SearchHandler.Result = new("busy", null, false, [], 10);
                await submit.FocusAsync();
                await submit.PressAsync("Tab");
                await Expect(fallback).ToBeFocusedAsync();
                await fallback.PressAsync("Enter");
                await Expect(page.Locator("#player-search-message")).ToHaveAttributeAsync("data-search-state", "busy");
                await Expect(fallback).ToHaveCountAsync(0);
                await Expect(input).ToHaveValueAsync("NoMatch");
                await Expect(page.Locator("#player-search-match")).ToHaveValueAsync("contains");
                factory.SearchHandler.LastName.Should().Be("NoMatch");
                factory.SearchHandler.LastMatch.Should().Be("contains");
                factory.SearchHandler.RequestCount.Should().Be(calls + 2);
                if (scripts)
                {
                    await Expect(submit).ToBeDisabledAsync();
                    await input.PressAsync("Enter");
                    factory.SearchHandler.RequestCount.Should().Be(calls + 2);
                    await page.Clock.FastForwardAsync(10_000);
                }
                await Expect(submit).ToBeEnabledAsync();
                factory.SearchHandler.RequestCount.Should().Be(calls + 2);
                factory.SearchHandler.Status = HttpStatusCode.OK;
                factory.SearchHandler.Result = new("fresh", DateTimeOffset.UtcNow, false,
                    [new(1, "prefixNoMatchtail", 1, 25, 8)], null);
                await input.PressAsync("Enter");
                await Expect(page.Locator(".player-search-result h3")).ToHaveTextAsync("prefixNoMatchtail");
                await Expect(input).ToHaveValueAsync("NoMatch");
                await Expect(page.Locator("#player-search-match")).ToHaveValueAsync("contains");
                await Expect(fallback).ToHaveCountAsync(0);
                factory.SearchHandler.RequestCount.Should().Be(calls + 3);
                page.Url.Should().NotContain("NoMatch");
                AssertTokenFree(await page.ContentAsync());
            }
        }
    }
}
