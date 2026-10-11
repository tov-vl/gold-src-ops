using AwesomeAssertions;
using Microsoft.Playwright;

namespace GoldSrcOps.WebTests.Browser;

public sealed partial class BrowserTokenBoundaryTests
{
    [BrowserFact]
    public async Task Public_language_navigation_copy_and_mobile_layout_work_in_both_languages()
    {
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory();
        factory.StartServer();
        foreach (var width in new[] { 1440, 390, 320 })
        {
            await Page.SetViewportSizeAsync(width, 900);
            await Page.GotoAsync(new Uri(factory.ClientOptions.BaseAddress, "/play").AbsoluteUri);
            await Page.Locator(".public-language a[lang='en']").ClickAsync();
            await Expect(Page.Locator(".play-page")).ToHaveAttributeAsync("lang", "en");
            await Expect(Page).ToHaveTitleAsync("Play CS 1.6 - GoldSrcOps");
            (await Page.Locator(".play-page").InnerTextAsync()).Should().NotMatchRegex("[А-Яа-я]");
            await Page.EvaluateAsync("() => { navigator.clipboard.writeText = async value => { window.__copiedCommand = value; }; }");
            await Page.Locator(".join-panel__copy").ClickAsync();
            await Expect(Page.Locator("#play-copy-status")).ToHaveTextAsync("Connection command copied.");
            (await Page.EvaluateAsync<string>("window.__copiedCommand")).Should().Be("connect play.example.test:27015");
            await Page.EvaluateAsync("() => { navigator.clipboard.writeText = async () => { throw new Error('denied'); }; }");
            await Page.Locator(".join-panel__copy").ClickAsync();
            await Expect(Page.Locator("#play-copy-status")).ToHaveTextAsync("Could not copy. Select the command and copy it manually.");
            (await Page.EvaluateAsync<bool>("document.documentElement.scrollWidth > document.documentElement.clientWidth")).Should().BeFalse();
            await CaptureScreenshotIfRequestedAsync("player-guide-en", new() { Width = width, Height = 900 });
            await Page.Locator(".primary-nav a[href='/leaderboard?lang=en']").ClickAsync();
            await Expect(Page.Locator(".leaderboard-page")).ToHaveAttributeAsync("lang", "en");
            await Expect(Page.Locator(".leaderboard-rank").First).ToHaveTextAsync("Legend");
            await Expect(Page.Locator(".primary-nav a[href='/leaderboard?lang=en']")).ToHaveAttributeAsync("aria-current", "page");
            (await Page.EvaluateAsync<bool>("document.documentElement.scrollWidth > document.documentElement.clientWidth")).Should().BeFalse();
            await CaptureScreenshotIfRequestedAsync("leaderboard-en", new() { Width = width, Height = 900 });
            await Page.Locator(".public-language a[lang='ru']").ClickAsync();
            await Expect(Page.Locator(".leaderboard-page")).ToHaveAttributeAsync("lang", "ru");
            await Page.Locator(".leaderboard-join").ClickAsync();
            await Expect(Page.Locator(".play-page")).ToHaveAttributeAsync("lang", "ru");
            await AssertBrowserStorageIsEmptyAsync();
            (await Context.CookiesAsync()).Should().BeEmpty();
        }
    }

    [BrowserFact]
    public async Task Public_language_selection_and_refresh_work_without_javascript()
    {
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory();
        factory.StartServer();
        await using var context = await Browser.NewContextAsync(new() { JavaScriptEnabled = false, ViewportSize = new() { Width = 320, Height = 740 } });
        var page = await context.NewPageAsync();
        await page.GotoAsync(new Uri(factory.ClientOptions.BaseAddress, "/play").AbsoluteUri);
        await page.Locator(".public-language a[lang='en']").ClickAsync();
        await page.Locator("[data-play-refresh]").ClickAsync();
        await Expect(page.Locator(".play-page")).ToHaveAttributeAsync("lang", "en");
        await Expect(page.Locator("#play-refresh-status")).ToHaveTextAsync("Select \"Refresh status\" to check for an update.");
        await page.Locator(".primary-nav a[href='/leaderboard?lang=en']").ClickAsync();
        await page.Locator("[data-leaderboard-refresh]").ClickAsync();
        await Expect(page.Locator(".leaderboard-page")).ToHaveAttributeAsync("lang", "en");
        await Expect(page.Locator("#leaderboard-refresh-status")).ToHaveTextAsync("Select \"Refresh\" to load a new snapshot.");
        await page.Locator(".leaderboard-join").ClickAsync();
        await Expect(page).ToHaveTitleAsync("Play CS 1.6 - GoldSrcOps");
        await page.Locator(".public-language a[lang='ru']").ClickAsync();
        await Expect(page.Locator(".play-page")).ToHaveAttributeAsync("lang", "ru");
        (await context.CookiesAsync()).Should().BeEmpty();
    }

    [BrowserFact]
    public async Task English_refresh_retains_language_through_failure_mismatch_and_recovery_without_credentials()
    {
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory();
        factory.StartServer();
        await Page.GotoAsync(new Uri(factory.ClientOptions.BaseAddress, BrowserTokenBoundaryWebApplicationFactory.SignInPath).AbsoluteUri);
        (await Context.CookiesAsync()).Should().Contain(cookie => cookie.Name == BrowserTokenBoundaryWebApplicationFactory.AuthenticationCookieName);
        var now = DateTime.UtcNow;
        await Page.Clock.InstallAsync(new() { TimeDate = now });
        await Page.Clock.PauseAtAsync(now.AddSeconds(10));
        foreach (var name in new[] { "play", "leaderboard" })
        {
            var routePath = $"/{name}?lang=en";
            await Page.GotoAsync(new Uri(factory.ClientOptions.BaseAddress, routePath).AbsoluteUri);
            var html = await Page.ContentAsync();
            var requests = new List<IRequest>();
            await Page.RouteAsync("**" + routePath, async route =>
            {
                requests.Add(route.Request);
                await route.FulfillAsync(new() { ContentType = "text/html", Body = requests.Count == 2 ? html.Replace("lang=\"en\"", "lang=\"ru\"", StringComparison.Ordinal) : html, Status = requests.Count == 1 ? 503 : 200 });
            });
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                await Page.RunAndWaitForResponseAsync(() => Page.Clock.FastForwardAsync(60_000), response => response.Url.EndsWith(routePath, StringComparison.Ordinal));
                await Expect(Page.Locator($"#{name}-refresh-status")).ToHaveTextAsync(attempt < 3
                    ? (string.Equals(name, "play", StringComparison.Ordinal) ? "Could not refresh the status. We will try again in a minute; you can also refresh manually." : "Could not refresh the leaderboard. We will try again in a minute; you can also refresh manually.")
                    : (string.Equals(name, "play", StringComparison.Ordinal) ? "Check complete." : "Check complete. Fresh snapshot received."));
                await Expect(Page.Locator(string.Equals(name, "play", StringComparison.Ordinal) ? ".join-panel__state" : ".leaderboard-state")).ToHaveTextAsync(attempt < 3
                    ? (string.Equals(name, "play", StringComparison.Ordinal) ? "Current status is unconfirmed" : "Stale snapshot")
                    : (string.Equals(name, "play", StringComparison.Ordinal) ? "Server is responding" : "Fresh snapshot"));
                requests.Count.Should().Be(attempt);
            }
            await Page.Clock.FastForwardAsync(5_000);
            await Page.RunAndWaitForResponseAsync(() => Page.Locator($"[data-{name}-refresh]").ClickAsync(), response => response.Url.EndsWith(routePath, StringComparison.Ordinal));
            await Expect(Page.Locator($"#{name}-refresh-status")).ToHaveTextAsync(string.Equals(name, "play", StringComparison.Ordinal) ? "Check complete." : "Check complete. Fresh snapshot received.");
            requests.Count.Should().Be(4);
            foreach (var request in requests) await AssertAnonymousRefreshAsync(request);
            await Expect(Page.Locator($".{name}-page")).ToHaveAttributeAsync("lang", "en");
            AssertTokenFree(await Page.ContentAsync());
            await AssertBrowserStorageIsEmptyAsync();
            await Page.UnrouteAllAsync();
        }
    }
}
