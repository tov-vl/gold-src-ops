using AwesomeAssertions;
using Microsoft.Playwright;

namespace GoldSrcOps.WebTests.Browser;

public sealed partial class BrowserTokenBoundaryTests
{
    [BrowserFact]
    public async Task Public_status_language_range_and_navigation_work_on_mobile_and_without_javascript()
    {
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory();
        factory.StartServer();
        foreach (var javascript in new[] { true, false })
        {
            await using var context = await Browser.NewContextAsync(new() { JavaScriptEnabled = javascript });
            var page = await context.NewPageAsync();
            foreach (var width in new[] { 1440, 390, 320 })
            {
                await page.SetViewportSizeAsync(width, 900);
                await page.GotoAsync(new Uri(factory.ClientOptions.BaseAddress, "/?range=7d").AbsoluteUri);
                await Expect(page.Locator(".site-shell")).ToHaveAttributeAsync("lang", "ru");
                await Expect(page).ToHaveTitleAsync("Статус GoldSrcOps");
                await page.Locator(".public-language a[lang='en']").ClickAsync();
                await Expect(page).ToHaveURLAsync(new Uri(factory.ClientOptions.BaseAddress, "/?range=7d&lang=en").AbsoluteUri);
                await Expect(page.Locator(".range-switch a[aria-current='page']")).ToHaveTextAsync("7 days");
                await Expect(page.Locator(".primary-nav a").First).ToHaveAttributeAsync("aria-current", "page");
                await Expect(page.Locator(".site-shell")).ToHaveAttributeAsync("lang", "en");
                (await page.Locator(".site-shell").InnerTextAsync()).Should().NotMatchRegex("[А-Яа-я]");
                (await page.Locator(".history-bar").First.GetAttributeAsync("aria-label")).Should().Contain("Bucket starting");
                if (javascript)
                {
                    await page.EvaluateAsync("() => { navigator.clipboard.writeText = async value => { window.__copiedCommand = value; }; }");
                    await page.Locator(".public-server__copy").ClickAsync();
                    await Expect(page.Locator("#public-server-copy-status")).ToHaveTextAsync("Connection command copied.");
                    (await page.EvaluateAsync<string>("window.__copiedCommand")).Should().Be("connect play.example.test:27015");
                    await page.EvaluateAsync("() => { navigator.clipboard.writeText = async () => { throw new Error('denied'); }; }");
                    await page.Locator(".public-server__copy").ClickAsync();
                    await Expect(page.Locator("#public-server-copy-status")).ToHaveTextAsync("Could not copy. Select the command and copy it manually.");
                }
                await AssertFitsAsync();
                await page.Locator(".range-switch a").First.ClickAsync();
                await Expect(page).ToHaveURLAsync(new Uri(factory.ClientOptions.BaseAddress, "/?range=24h&lang=en").AbsoluteUri);
                await page.Locator(".public-language a[lang='ru']").ClickAsync();
                await Expect(page.Locator(".range-switch a[aria-current='page']")).ToHaveTextAsync("24 часа");
                await Expect(page.Locator("footer")).ToContainTextAsync("Время указано в UTC");
                if (javascript)
                {
                    await page.EvaluateAsync("() => { navigator.clipboard.writeText = async () => {}; }");
                    await page.Locator(".public-server__copy").ClickAsync();
                    await Expect(page.Locator("#public-server-copy-status")).ToHaveTextAsync("Команда подключения скопирована.");
                }
                await AssertFitsAsync();
                foreach (var language in new[] { "en", "ru" })
                {
                    await page.Locator($".public-language a[lang='{language}']").ClickAsync();
                    var suffix = string.Equals(language, "en", StringComparison.Ordinal) ? "?lang=en" : string.Empty;
                    await page.Locator($".public-server__connect a[href='/play{suffix}']").ClickAsync();
                    await page.Locator(".play-intro__link").ClickAsync();
                    await Expect(page).ToHaveURLAsync(new Uri(factory.ClientOptions.BaseAddress, "/play" + suffix + "#first-steps").AbsoluteUri);
                    await page.Locator($".primary-nav a[href='/leaderboard{suffix}']").ClickAsync();
                    await page.Locator(".brand").ClickAsync();
                    await Expect(page.Locator(".status-page")).ToHaveAttributeAsync("lang", language);
                }
                (await context.CookiesAsync()).Should().BeEmpty();
            }

            async Task AssertFitsAsync()
            {
                await page.WaitForLoadStateAsync(LoadState.Load);
                var overflow = await page.EvaluateAsync<string[]>("Array.from(document.querySelectorAll('body *')).filter(e => e.getBoundingClientRect().right > document.documentElement.clientWidth + 0.5).map(e => e.tagName + '.' + e.className + ': ' + e.getBoundingClientRect().width)");
                (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > document.documentElement.clientWidth"))
                    .Should().BeFalse("page {0} must fit width {1}; overflowing elements: {2}", page.Url, page.ViewportSize?.Width, string.Join(", ", overflow));
            }
        }
    }
}
