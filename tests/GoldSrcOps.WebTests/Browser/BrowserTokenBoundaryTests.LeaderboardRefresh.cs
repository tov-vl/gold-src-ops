using AwesomeAssertions;
using Microsoft.Playwright;

namespace GoldSrcOps.WebTests.Browser;

public sealed partial class BrowserTokenBoundaryTests
{
    [BrowserFact]
    public async Task Leaderboard_manual_refresh_replaces_only_results_and_keeps_focus_and_encoded_names()
    {
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory();
        factory.StartServer();
        await Page.GotoAsync(new Uri(factory.ClientOptions.BaseAddress, BrowserTokenBoundaryWebApplicationFactory.SignInPath).AbsoluteUri);
        (await Context.CookiesAsync()).Should().Contain(cookie => cookie.Name == BrowserTokenBoundaryWebApplicationFactory.AuthenticationCookieName);
        await Page.GotoAsync(new Uri(factory.ClientOptions.BaseAddress, "/leaderboard").AbsoluteUri);
        var body = await Page.ContentAsync();
        var requests = new List<IRequest>();
        await Page.RouteAsync("**/leaderboard", async route =>
        {
            requests.Add(route.Request);
            await route.FulfillAsync(new() { ContentType = "text/html", Body = body.Replace("Другой игрок", "Обновленный игрок", StringComparison.Ordinal) });
        });
        await Page.EvaluateAsync("window.__documentMarker = 42");
        await Page.Locator("[data-leaderboard-refresh]").ClickAsync();
        await Expect(Page.Locator("#leaderboard-refresh-status")).ToHaveTextAsync("Проверка завершена. Свежий снимок получен.");
        await Expect(Page.Locator(".leaderboard-table tbody th").Last).ToHaveTextAsync("Обновленный игрок");
        await Expect(Page.Locator("[data-leaderboard-refresh]")).ToBeFocusedAsync();
        (await Page.EvaluateAsync<int>("window.__documentMarker")).Should().Be(42);
        await Expect(Page.Locator(".leaderboard-table tbody th").First).ToHaveTextAsync("Игрок <script>alert(1)</script>");
        await AssertAnonymousRefreshAsync(requests.Should().ContainSingle().Which);
        AssertTokenFree(await Page.ContentAsync());
        await AssertBrowserStorageIsEmptyAsync();
    }

    [BrowserFact]
    public async Task Leaderboard_automatic_failure_marks_old_rows_stale_and_next_pass_recovers()
    {
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory();
        factory.StartServer();
        await InstallLeaderboardClockAsync(factory);
        var body = await Page.ContentAsync();
        var requests = 0;
        await Page.RouteAsync("**/leaderboard", async route =>
        {
            requests++;
            await route.FulfillAsync(requests == 1
                ? new() { Status = 503, ContentType = "text/plain", Body = "fixture failure" }
                : new() { ContentType = "text/html", Body = body });
        });
        await Page.Clock.RunForAsync(60_000);
        await Expect(Page.Locator(".leaderboard-state")).ToHaveTextAsync("Устаревший снимок");
        await Expect(Page.Locator(".leaderboard-table tbody tr")).ToHaveCountAsync(2);
        await Expect(Page.Locator("[data-leaderboard-notice]")).ToBeVisibleAsync();
        await Page.Clock.RunForAsync(60_000);
        await Expect(Page.Locator(".leaderboard-state")).ToHaveTextAsync("Свежий снимок");
        await Expect(Page.Locator("[data-leaderboard-notice]")).ToBeHiddenAsync();
        requests.Should().Be(2);
    }

    [BrowserFact]
    public async Task Leaderboard_hidden_tab_stops_requests_and_resume_expires_then_refreshes()
    {
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory();
        factory.StartServer();
        await InstallLeaderboardClockAsync(factory);
        var body = await Page.ContentAsync();
        var requests = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Page.RouteAsync("**/leaderboard", async route =>
        {
            requests++;
            entered.TrySetResult();
            await release.Task;
            await route.FulfillAsync(new() { ContentType = "text/html", Body = body });
        });
        await SetLeaderboardHiddenAsync(true);
        await Page.Clock.RunForAsync(240_000);
        requests.Should().Be(0);
        await SetLeaderboardHiddenAsync(false);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Expect(Page.Locator(".leaderboard-state")).ToHaveTextAsync("Устаревший снимок");
        release.SetResult();
        await Expect(Page.Locator(".leaderboard-state")).ToHaveTextAsync("Свежий снимок");
        requests.Should().Be(1);
    }

    [BrowserFact]
    public async Task Leaderboard_timeout_has_no_overlap_or_immediate_retry_and_age_expires()
    {
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory();
        factory.StartServer();
        await InstallLeaderboardClockAsync(factory);
        var requests = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Page.RouteAsync("**/leaderboard", async route =>
        {
            requests++;
            entered.TrySetResult();
            await release.Task;
            await route.AbortAsync();
        });
        await Page.Locator("[data-leaderboard-refresh]").ClickAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Page.Locator("[data-leaderboard-refresh]").DispatchEventAsync("click");
        await Page.Clock.RunForAsync(9_000);
        await Expect(Page.Locator(".leaderboard-state")).ToHaveTextAsync("Устаревший снимок");
        requests.Should().Be(1);
        release.SetResult();
        await SetLeaderboardHiddenAsync(true);
        await Page.Clock.FastForwardAsync(86_401_000);
        await SetLeaderboardHiddenAsync(false);
        await Expect(Page.Locator(".leaderboard-state")).ToHaveTextAsync("Рейтинг временно недоступен");
        await Expect(Page.Locator(".leaderboard-table")).ToHaveCountAsync(0);
    }

    [BrowserFact]
    public async Task Leaderboard_rejects_malformed_oversized_and_non_html_refreshes_then_accepts_empty_and_unavailable()
    {
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory();
        factory.StartServer();
        await InstallLeaderboardClockAsync(factory);
        var body = await Page.ContentAsync();
        var responses = new Queue<RouteFulfillOptions>([
            new() { ContentType = "text/html", Body = "<html>missing panel</html>" },
            new() { ContentType = "text/html", Body = body + new string('x', 131_072) },
            new() { ContentType = "application/json", Body = "{}" }
        ]);
        await Page.RouteAsync("**/leaderboard", async route => await route.FulfillAsync(responses.Dequeue()));
        for (var i = 0; i < 3; i++)
        {
            await Page.Clock.RunForAsync(5_000);
            await Page.Locator("[data-leaderboard-refresh]").ClickAsync();
            await Expect(Page.Locator("#leaderboard-refresh-status")).ToContainTextAsync("Не удалось обновить");
            await Expect(Page.Locator(".leaderboard-state")).ToHaveTextAsync("Устаревший снимок");
        }
        // Fetch an actual SSR unavailable projection rather than fabricating its rows.
        await using var unavailable = new BrowserTokenBoundaryWebApplicationFactory(leaderboard: new("unavailable", null, []));
        unavailable.StartServer();
        using var client = unavailable.CreateClient();
        var unavailableBody = await client.GetStringAsync("/leaderboard");
        responses.Clear();
        responses.Enqueue(new() { ContentType = "text/html", Body = unavailableBody });
        await Page.Clock.RunForAsync(5_000);
        await Page.Locator("[data-leaderboard-refresh]").ClickAsync();
        await Expect(Page.Locator(".leaderboard-state")).ToHaveTextAsync("Рейтинг временно недоступен");
        await Expect(Page.Locator(".leaderboard-table")).ToHaveCountAsync(0);
        await using var empty = new BrowserTokenBoundaryWebApplicationFactory(leaderboard: new("fresh", DateTimeOffset.UtcNow, []));
        empty.StartServer();
        using var emptyClient = empty.CreateClient();
        responses.Enqueue(new() { ContentType = "text/html", Body = await emptyClient.GetStringAsync("/leaderboard") });
        await Page.Clock.RunForAsync(5_000);
        await Page.Locator("[data-leaderboard-refresh]").ClickAsync();
        await Expect(Page.Locator(".leaderboard-state")).ToHaveTextAsync("Свежий снимок");
        await Expect(Page.Locator(".leaderboard-empty")).ToHaveTextAsync("В рейтинге пока нет результатов.");
    }

    [BrowserFact]
    public async Task Leaderboard_without_javascript_keeps_manual_reload_and_server_rendered_data()
    {
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory();
        factory.StartServer();
        await using var context = await Browser.NewContextAsync(new() { JavaScriptEnabled = false, ViewportSize = new() { Width = 320, Height = 740 } });
        var page = await context.NewPageAsync();
        await page.GotoAsync(new Uri(factory.ClientOptions.BaseAddress, "/leaderboard").AbsoluteUri);
        await Expect(page.Locator(".leaderboard-table tbody tr")).ToHaveCountAsync(2);
        await page.Locator("[data-leaderboard-refresh]").ClickAsync();
        await Expect(page.Locator(".leaderboard-state")).ToHaveTextAsync("Свежий снимок");
        (await page.Locator("[data-leaderboard-refresh]").GetAttributeAsync("href")).Should().Be("/leaderboard");
    }

    private async Task InstallLeaderboardClockAsync(BrowserTokenBoundaryWebApplicationFactory factory)
    {
        var now = DateTime.UtcNow;
        await Page.Clock.InstallAsync(new() { TimeDate = now });
        await Page.GotoAsync(new Uri(factory.ClientOptions.BaseAddress, "/leaderboard").AbsoluteUri);
        await Page.Clock.PauseAtAsync(now.AddSeconds(10));
    }

    private async Task SetLeaderboardHiddenAsync(bool hidden) => await Page.EvaluateAsync("""
        hidden => {
            Object.defineProperty(document, "hidden", { configurable: true, get: () => hidden });
            document.dispatchEvent(new Event("visibilitychange"));
        }
        """, hidden);
}
