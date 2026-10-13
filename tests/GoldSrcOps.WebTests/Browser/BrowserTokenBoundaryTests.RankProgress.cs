using AwesomeAssertions;
using GoldSrcOps.Contracts.Monitoring;
using Microsoft.Playwright;

namespace GoldSrcOps.WebTests.Browser;

public sealed partial class BrowserTokenBoundaryTests
{
    [BrowserFact]
    public async Task Rank_progress_uses_stale_snapshot_in_both_languages_without_scripts_and_fits_enlarged_text()
    {
        PublicLeaderboardEntryResponse[] entries =
            [new(1, "First", 4, 500, 1), new(2, "Second", 3, 250, 1), new(3, "Third", 2, 100, 1),
             new(4, "Fourth", 1, 37, 1), new(5, "Fifth", 0, 24, 1)];
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory(leaderboard: new("stale", DateTimeOffset.UtcNow.AddMinutes(-4), entries));
        factory.StartServer();
        foreach (var scripts in new[] { true, false })
        {
            await using var context = await Browser.NewContextAsync(new() { JavaScriptEnabled = scripts });
            var page = await context.NewPageAsync();
            foreach (var language in new[] { "ru", "en" })
            {
                var suffix = string.Equals(language, "en", StringComparison.Ordinal) ? "?lang=en" : "";
                await page.GotoAsync(new Uri(factory.ClientOptions.BaseAddress, "/leaderboard" + suffix).AbsoluteUri);
                await Expect(page.Locator(".leaderboard-rank-progress")).ToHaveTextAsync(
                    string.Equals(language, "en", StringComparison.Ordinal)
                        ? ["Highest rank", "To Legend: 250", "To Elite: 150", "To Veteran: 63", "To Fighter: 1"]
                        : ["Высший ранг", "До Легенды: 250", "До Элиты: 150", "До Ветерана: 63", "До Бойца: 1"]);
                await Expect(page.Locator("[data-leaderboard-notice]")).ToBeVisibleAsync();
                await Expect(page.Locator(".leaderboard-progress-hint")).ToBeVisibleAsync();
                foreach (var width in new[] { 320, 390, 1440 })
                {
                    await page.SetViewportSizeAsync(width, 900);
                    await page.EvaluateAsync("document.documentElement.style.fontSize = '200%'");
                    (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > document.documentElement.clientWidth")).Should().BeFalse();
                    await page.EvaluateAsync("document.documentElement.style.fontSize = ''");
                    if (scripts && Environment.GetEnvironmentVariable(ScreenshotDirectoryVariable) is { Length: > 0 } directory)
                    {
                        Directory.CreateDirectory(directory);
                        await page.Locator(".leaderboard-panel").ScreenshotAsync(new() { Path = Path.Combine(directory, $"rank-progress-{language}-{width}.png") });
                    }
                }
                await page.Locator(".leaderboard-rank-guide").FocusAsync();
                await page.Keyboard.PressAsync("Enter");
                await Expect(page.Locator("#ranks")).ToBeFocusedAsync();
                AssertTokenFree(await page.ContentAsync());
            }
        }
    }
}
