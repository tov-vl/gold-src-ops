using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace GoldSrcOps.WebTests.Browser;

public sealed partial class BrowserTokenBoundaryTests
{
    [BrowserFact]
    public async Task Rank_guide_matches_addon_and_supports_keyboard_without_scripts_or_live_data()
    {
        foreach (var missing in new[] { false, true })
        {
            await using var factory = new BrowserTokenBoundaryWebApplicationFactory(
                serverJoinFailureStatus: missing ? HttpStatusCode.ServiceUnavailable : null,
                leaderboardFailure: missing ? HttpStatusCode.ServiceUnavailable : null);
            factory.StartServer();
            var root = factory.Services.GetRequiredService<IWebHostEnvironment>().ContentRootPath;
            var addon = await File.ReadAllLinesAsync(Path.Combine(root, "../../samples/amxmodx-weapon-selection/goldsrcops_weapon_selection.sma"));
            var thresholds = addon.Single(line => line.StartsWith("new const RANK_KILLS[] =", StringComparison.Ordinal))
                .Split('{')[1].Split('}')[0].Split(',', StringSplitOptions.TrimEntries);
            foreach (var scripts in new[] { true, false })
            {
                await using var context = await Browser.NewContextAsync(new() { JavaScriptEnabled = scripts, ViewportSize = new() { Width = 320, Height = 900 } });
                var page = await context.NewPageAsync();
                foreach (var language in new[] { "ru", "en" })
                {
                    var suffix = string.Equals(language, "en", StringComparison.Ordinal) ? "?lang=en" : "";
                    await page.GotoAsync(new Uri(factory.ClientOptions.BaseAddress, "/leaderboard" + suffix).AbsoluteUri);
                    await page.Locator(".leaderboard-rank-guide").FocusAsync();
                    await page.Keyboard.PressAsync("Enter");
                    await Expect(page).ToHaveURLAsync(new Uri(factory.ClientOptions.BaseAddress, "/play" + suffix + "#ranks").AbsoluteUri);
                    await Expect(page.Locator("#ranks")).ToBeFocusedAsync();
                    await Expect(page.Locator(".rank-guide-table tbody td")).ToHaveTextAsync(thresholds);
                    await Expect(page.Locator(".rank-guide-table tbody th[scope='row']")).ToHaveTextAsync(
                        string.Equals(language, "en", StringComparison.Ordinal)
                            ? ["Recruit", "Fighter", "Veteran", "Elite", "Legend"]
                            : ["Новичок", "Боец", "Ветеран", "Элита", "Легенда"]);
                    await Expect(page.Locator(".rank-guide-table caption")).ToBeVisibleAsync();
                    if (string.Equals(language, "en", StringComparison.Ordinal))
                        (await page.Locator("#ranks").InnerTextAsync()).Should().NotMatchRegex("[А-Яа-я]");
                    await page.Keyboard.PressAsync("Tab");
                    await Expect(page.Locator("a:focus")).ToHaveCountAsync(1);
                    foreach (var width in new[] { 320, 390, 1440 })
                    {
                        await page.SetViewportSizeAsync(width, 900);
                        await page.EvaluateAsync("document.documentElement.style.fontSize = '200%'");
                        (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > document.documentElement.clientWidth")).Should().BeFalse();
                    }
                    AssertTokenFree(await page.ContentAsync());
                    if (scripts && !missing && Environment.GetEnvironmentVariable(ScreenshotDirectoryVariable) is { Length: > 0 } directory)
                    {
                        Directory.CreateDirectory(directory);
                        await page.EvaluateAsync("document.documentElement.style.fontSize = ''");
                        await page.Locator("#ranks").ScreenshotAsync(new() { Path = Path.Combine(directory, $"ranks-{language}-desktop.png") });
                        await page.SetViewportSizeAsync(320, 900);
                        await page.Locator("#ranks").ScreenshotAsync(new() { Path = Path.Combine(directory, $"ranks-{language}-mobile.png") });
                    }
                }
            }
        }
    }
}
