using AwesomeAssertions;
using Microsoft.Playwright;

namespace GoldSrcOps.WebTests.Browser;

public sealed partial class BrowserTokenBoundaryTests
{
    [BrowserFact]
    public async Task Public_keyboard_skip_preserves_url_and_reaches_content_with_and_without_scripts()
    {
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory();
        factory.StartServer();
        foreach (var scripts in new[] { true, false })
        {
            await using var context = await Browser.NewContextAsync(new() { JavaScriptEnabled = scripts, ViewportSize = new() { Width = 320, Height = 900 } });
            var page = await context.NewPageAsync();
            foreach (var language in new[] { "ru", "en" })
                foreach (var path in new[] { "/?range=7d", "/play?", "/leaderboard?" })
                {
                    var url = new Uri(factory.ClientOptions.BaseAddress, path + "&lang=" + language).AbsoluteUri;
                    await page.GotoAsync(url);
                    await Expect(page.Locator("html")).ToHaveAttributeAsync("lang", language);
                    await page.Keyboard.PressAsync("Tab");
                    await Expect(page.Locator(".skip-link")).ToBeFocusedAsync();
                    await Expect(page.Locator(".skip-link")).ToHaveTextAsync(string.Equals(language, "en", StringComparison.Ordinal) ? "Skip to content" : "Перейти к содержимому");
                    var bounds = await page.Locator(".skip-link").BoundingBoxAsync();
                    bounds!.Y.Should().BeGreaterThanOrEqualTo(0);
                    (bounds.X + bounds.Width).Should().BeLessThanOrEqualTo(320);
                    await page.Keyboard.PressAsync("Enter");
                    await Expect(page).ToHaveURLAsync(url + "#main-content");
                    await Expect(page.Locator("main")).ToBeFocusedAsync();
                    await page.Keyboard.PressAsync("Tab");
                    (await page.EvaluateAsync<bool>("document.activeElement.closest('main') !== null")).Should().BeTrue();
                    // Follow the normal tab sequence through content controls; no scripted focus jump.
                    var reachedRefresh = false;
                    for (var i = 0; i < 10; i++)
                    {
                        reachedRefresh = await page.EvaluateAsync<bool>("document.activeElement.matches('[data-status-refresh], [data-play-refresh], [data-leaderboard-refresh]')");
                        if (reachedRefresh) break;
                        await page.Keyboard.PressAsync("Tab");
                    }
                    reachedRefresh.Should().BeTrue();
                    (await page.EvaluateAsync<double>("parseFloat(getComputedStyle(document.activeElement).outlineWidth)")).Should().BeGreaterThan(0);
                    await page.Keyboard.PressAsync("Enter");
                    if (scripts)
                    {
                        await Expect(page.Locator("[data-refresh-announcement]")).Not.ToBeEmptyAsync();
                        (await page.EvaluateAsync<bool>("document.activeElement.matches('[data-status-refresh], [data-play-refresh], [data-leaderboard-refresh]')")).Should().BeTrue();
                        if (await page.Locator("[data-copy-connect]").CountAsync() > 0)
                        {
                            await page.EvaluateAsync("navigator.clipboard.writeText = async value => { window.__keyboardCopy = value; }");
                            for (var i = 0; i < 12; i++)
                            {
                                if (await page.EvaluateAsync<bool>("document.activeElement.matches('[data-copy-connect]')")) break;
                                await page.Keyboard.PressAsync("Tab");
                            }
                            await Expect(page.Locator("[data-copy-connect]")).ToBeFocusedAsync();
                            await page.Keyboard.PressAsync("Enter");
                            (await page.EvaluateAsync<string>("window.__keyboardCopy")).Should().Be("connect play.example.test:27015");
                        }
                    }
                    else
                    {
                        await Expect(page.Locator("html")).ToHaveAttributeAsync("lang", language);
                    }
                }
        }
    }

    [BrowserFact]
    public async Task Public_pages_reflow_with_double_text_and_keep_keyboard_controls_visible()
    {
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory();
        factory.StartServer();
        foreach (var width in new[] { 1440, 720, 320 })
            foreach (var language in new[] { "ru", "en" })
                foreach (var path in new[] { "/", "/play", "/leaderboard" })
                {
                    await Page.SetViewportSizeAsync(width, 900);
                    await Page.GotoAsync(new Uri(factory.ClientOptions.BaseAddress, path + "?lang=" + language).AbsoluteUri);
                    await Page.EvaluateAsync("document.documentElement.style.fontSize = '200%'");
                    var overflow = await Page.EvaluateAsync<int>("document.documentElement.scrollWidth - document.documentElement.clientWidth");
                    overflow.Should().BeLessThanOrEqualTo(1, $"{path} {language} at {width}px with double text");
                    // Text must wrap instead of being concealed by clipping containers.
                    var clipped = await Page.EvaluateAsync<string[]>("""
                [...document.querySelectorAll('main a, main button, .primary-nav a, main h1, main h2')]
                  .filter(e => e.clientWidth && (e.scrollWidth > e.clientWidth + 1 || (['hidden', 'clip'].includes(getComputedStyle(e).overflowY) && e.scrollHeight > e.clientHeight + 1)))
                  .map(e => e.textContent.trim())
                """);
                    clipped.Should().BeEmpty($"{path} {language} at {width}px");
                    await Page.Locator("[data-status-refresh], [data-play-refresh], [data-leaderboard-refresh]").FocusAsync();
                    var box = await Page.Locator("[data-status-refresh], [data-play-refresh], [data-leaderboard-refresh]").BoundingBoxAsync();
                    box!.X.Should().BeGreaterThanOrEqualTo(0);
                    (box.X + box.Width).Should().BeLessThanOrEqualTo(width);
                    await CaptureScreenshotIfRequestedAsync($"accessibility-{path.Trim('/').Replace("/", "-", StringComparison.Ordinal)}-{language}", new() { Width = width, Height = 900 });
                }
    }

    [BrowserFact]
    public async Task Public_refresh_announces_manual_results_and_state_changes_without_routine_timer_noise()
    {
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory();
        factory.StartServer();
        foreach (var name in new[] { "status", "play", "leaderboard" })
        {
            await using var context = await Browser.NewContextAsync();
            var page = await context.NewPageAsync();
            var now = DateTime.UtcNow;
            await page.Clock.InstallAsync(new() { TimeDate = now });
            var path = string.Equals(name, "status", StringComparison.Ordinal) ? "/?range=24h&lang=en" : $"/{name}?lang=en";
            await page.GotoAsync(new Uri(factory.ClientOptions.BaseAddress, path).AbsoluteUri);
            await page.Clock.PauseAtAsync(now.AddSeconds(10));
            var body = await page.ContentAsync();
            var fail = false;
            await page.RouteAsync("**" + path, async route =>
            {
                if (fail) await route.FulfillAsync(new() { Status = 503 });
                else await route.FulfillAsync(new() { ContentType = "text/html", Body = body });
            });
            await page.EvaluateAsync("""
                window.__announcements = [];
                new MutationObserver(() => window.__announcements.push(document.querySelector('[data-refresh-announcement]').textContent))
                  .observe(document.querySelector('[data-refresh-announcement]'), { childList: true, subtree: true, characterData: true });
                """);
            var button = page.Locator($"[data-{name}-refresh]");
            await button.FocusAsync();
            for (var i = 0; i < 2; i++)
            {
                await page.RunAndWaitForResponseAsync(() => page.Clock.FastForwardAsync(60_000), r => r.Url.EndsWith(path, StringComparison.Ordinal));
                await Expect(button).Not.ToHaveAttributeAsync("aria-disabled", "true");
            }
            (await page.EvaluateAsync<int>("window.__announcements.length")).Should().Be(0);
            fail = true;
            await page.RunAndWaitForResponseAsync(() => page.Clock.FastForwardAsync(60_000), r => r.Url.EndsWith(path, StringComparison.Ordinal));
            await Expect(page.Locator("[data-refresh-announcement]")).Not.ToBeEmptyAsync();
            var failedMessage = await page.Locator("[data-refresh-announcement]").InnerTextAsync();
            await page.RunAndWaitForResponseAsync(() => page.Clock.FastForwardAsync(60_000), r => r.Url.EndsWith(path, StringComparison.Ordinal));
            await Expect(button).Not.ToHaveAttributeAsync("aria-disabled", "true");
            (await page.EvaluateAsync<int>("window.__announcements.length")).Should().Be(1);
            fail = false;
            await page.RunAndWaitForResponseAsync(() => page.Clock.FastForwardAsync(60_000), r => r.Url.EndsWith(path, StringComparison.Ordinal));
            await Expect(page.Locator("[data-refresh-announcement]")).Not.ToHaveTextAsync(failedMessage);
            await Expect(button).ToBeFocusedAsync();
            await page.Clock.FastForwardAsync(5_000);
            await page.Keyboard.PressAsync("Enter");
            await Expect(page.Locator("[data-refresh-announcement]")).ToContainTextAsync("Check complete.");
            await Expect(button).ToBeFocusedAsync();
            // Stable live region excludes age counters and replaced display panels.
            (await page.Locator("[data-refresh-announcement] time, [data-refresh-announcement] [data-play-age], [data-refresh-announcement] [data-leaderboard-age]").CountAsync()).Should().Be(0);
        }
    }
}
