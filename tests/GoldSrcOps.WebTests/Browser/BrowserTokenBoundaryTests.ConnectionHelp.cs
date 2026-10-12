using System.Net;
using AwesomeAssertions;
using Microsoft.Playwright;

namespace GoldSrcOps.WebTests.Browser;

public sealed partial class BrowserTokenBoundaryTests
{
    [BrowserFact]
    public async Task Connection_help_keyboard_links_survive_missing_data_and_disabled_scripts()
    {
        foreach (var missing in new[] { false, true })
        {
            await using var factory = new BrowserTokenBoundaryWebApplicationFactory(
                serverJoinFailureStatus: missing ? HttpStatusCode.ServiceUnavailable : null);
            factory.StartServer();
            foreach (var scripts in new[] { true, false })
            {
                await using var context = await Browser.NewContextAsync(new() { JavaScriptEnabled = scripts, ViewportSize = new() { Width = 320, Height = 900 } });
                var page = await context.NewPageAsync();
                foreach (var language in new[] { "ru", "en" })
                {
                    var path = string.Equals(language, "en", StringComparison.Ordinal) ? "/play?lang=en" : "/play";
                    var url = new Uri(factory.ClientOptions.BaseAddress, path).AbsoluteUri;
                    await page.GotoAsync(url);
                    await page.Locator(".join-panel__help a").FocusAsync();
                    await page.Keyboard.PressAsync("Enter");
                    await Expect(page).ToHaveURLAsync(url + "#connection-help");
                    await Expect(page.Locator("#connection-help")).ToBeFocusedAsync();
                    await page.Keyboard.PressAsync("Tab");
                    await Expect(page.Locator("#connection-help summary").First).ToBeFocusedAsync();
                    await page.Keyboard.PressAsync("Enter");
                    await Expect(page.Locator("#connection-help details").First).ToHaveAttributeAsync("open", "");
                    await page.Keyboard.PressAsync("Tab");
                    await page.Keyboard.PressAsync("Enter");
                    await Expect(page).ToHaveURLAsync(url + "#connection");
                    await Expect(page.Locator("#connection")).ToBeFocusedAsync();

                    var details = page.Locator("#connection-help details");
                    for (var i = 1; i < 4; i++)
                    {
                        await details.Nth(i).Locator("summary").FocusAsync();
                        await page.Keyboard.PressAsync("Enter");
                    }
                    await page.EvaluateAsync("document.documentElement.style.fontSize = '200%'");
                    (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > document.documentElement.clientWidth")).Should().BeFalse();
                    if (string.Equals(language, "en", StringComparison.Ordinal))
                        (await page.Locator("#connection-help").InnerTextAsync()).Should().NotMatchRegex("[А-Яа-я]");
                    if (missing)
                    {
                        await Expect(page.Locator(".join-panel__command")).ToHaveCountAsync(0);
                        await Expect(page.Locator("#connection-help details[open]")).ToHaveCountAsync(4);
                    }
                    else if (scripts)
                    {
                        await page.EvaluateAsync("() => { navigator.clipboard.writeText = async () => { throw new Error('fixture-denied'); }; }");
                        await page.Locator(".join-panel__copy").FocusAsync();
                        await page.Keyboard.PressAsync("Enter");
                        await Expect(page.Locator("#play-copy-status")).ToContainTextAsync(string.Equals(language, "en", StringComparison.Ordinal) ? "manually" : "вручную");
                        (await page.Locator(".join-panel__command").InnerTextAsync()).Should().Be("connect play.example.test:27015");
                    }
                    await details.Nth(2).Locator("a").FocusAsync();
                    await page.Keyboard.PressAsync("Enter");
                    await Expect(page.Locator("#check-connection")).ToBeFocusedAsync();
                    await page.Keyboard.PressAsync("Enter");
                    if (scripts)
                    {
                        await Expect(page.Locator("[data-refresh-announcement]")).Not.ToBeEmptyAsync();
                        await Expect(page.Locator("#connection-help details[open]")).ToHaveCountAsync(4);
                    }
                    else
                    {
                        await Expect(page).ToHaveURLAsync(url);
                        await details.Nth(3).Locator("summary").ClickAsync();
                    }
                    await details.Nth(3).Locator("a").FocusAsync();
                    await page.Keyboard.PressAsync("Enter");
                    await Expect(page).ToHaveURLAsync(url + "#first-steps");
                    await Expect(page.Locator("#first-steps")).ToBeFocusedAsync();
                    AssertTokenFree(await page.ContentAsync());
                    if (scripts && !missing && Environment.GetEnvironmentVariable(ScreenshotDirectoryVariable) is { Length: > 0 } directory)
                    {
                        Directory.CreateDirectory(directory);
                        await page.ScreenshotAsync(new() { Path = Path.Combine(directory, $"connection-help-{language}.png"), FullPage = true });
                    }
                }
            }
        }
    }
}
