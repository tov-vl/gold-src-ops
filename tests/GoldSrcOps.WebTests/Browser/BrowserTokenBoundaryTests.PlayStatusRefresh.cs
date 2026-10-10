using System.Net;
using AwesomeAssertions;
using GoldSrcOps.Contracts.Monitoring;
using GoldSrcOps.WebTests.Pages;
using Microsoft.Playwright;

namespace GoldSrcOps.WebTests.Browser;

public sealed partial class BrowserTokenBoundaryTests
{
    [BrowserFact]
    public async Task Player_status_manual_refresh_preserves_document_focus_and_copies_updated_address()
    {
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory();
        factory.StartServer();
        await Page.GotoAsync(new Uri(factory.ClientOptions.BaseAddress, BrowserTokenBoundaryWebApplicationFactory.SignInPath).AbsoluteUri);
        (await Context.CookiesAsync()).Should().Contain(cookie => cookie.Name == BrowserTokenBoundaryWebApplicationFactory.AuthenticationCookieName);
        await Page.GotoAsync(new Uri(factory.ClientOptions.BaseAddress, "/play").AbsoluteUri);
        var body = await PlayBodyAsync(PlayServer("online") with { Host = "new.example.test", Name = "Игрок <script>alert(1)</script>" });
        var requests = new List<IRequest>();
        await Page.RouteAsync("**/play", async route =>
        {
            requests.Add(route.Request);
            await route.FulfillAsync(new() { ContentType = "text/html", Body = body });
        });
        await Page.EvaluateAsync("window.__documentMarker = 42");
        await Page.Locator("[data-play-refresh]").ClickAsync();
        await Expect(Page.Locator("#play-refresh-status")).ToHaveTextAsync("Проверка завершена.");
        await Expect(Page.Locator("[data-play-refresh]")).ToBeFocusedAsync();
        await Expect(Page.Locator("#join-title")).ToHaveTextAsync("Игрок <script>alert(1)</script>");
        (await Page.EvaluateAsync<int>("window.__documentMarker")).Should().Be(42);
        await Page.EvaluateAsync("navigator.clipboard.writeText = async value => { window.__copiedCommand = value; }");
        await Page.Locator(".join-panel__copy").ClickAsync();
        await Expect(Page.Locator("#play-copy-status")).ToHaveTextAsync("Команда подключения скопирована.");
        (await Page.EvaluateAsync<string>("window.__copiedCommand")).Should().Be("connect new.example.test:27015");
        await AssertAnonymousRefreshAsync(requests.Should().ContainSingle().Which);
        AssertTokenFree(await Page.ContentAsync());
        await AssertBrowserStorageIsEmptyAsync();
    }

    [BrowserFact]
    public async Task Player_status_automatic_transitions_online_offline_unknown_unavailable_and_recovers()
    {
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory();
        factory.StartServer();
        await InstallPlayClockAsync(factory);
        var responses = new Queue<string>([
            await PlayBodyAsync(PlayServer("offline")),
            await PlayBodyAsync(PlayServer("unknown")),
            await PlayBodyAsync(null, HttpStatusCode.NotFound),
            await PlayBodyAsync(PlayServer("online"))
        ]);
        // Hold the response until the clock stops, independently of transport speed.
        var releaseFirstResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Page.RouteAsync("**/play", async route =>
        {
            await releaseFirstResponse.Task;
            await route.FulfillAsync(new() { ContentType = "text/html", Body = responses.Dequeue() });
        });
        await Page.Locator(".join-panel__copy").FocusAsync();
        await Page.Clock.FastForwardAsync(60_000);
        releaseFirstResponse.SetResult();
        await Expect(Page.Locator(".join-panel__state")).ToHaveTextAsync("Сервер не отвечает");
        await Expect(Page.Locator(".join-panel__copy")).ToBeFocusedAsync();
        await Expect(Page.Locator(".join-panel__facts dd").First).ToHaveTextAsync("Нет свежих данных");
        await Page.Clock.FastForwardAsync(60_000);
        await Expect(Page.Locator(".join-panel__state")).ToHaveTextAsync("Свежий статус не подтвержден");
        await Expect(Page.Locator("#play-refresh-status")).ToHaveTextAsync("Проверка завершена.");
        await Page.Clock.FastForwardAsync(60_000);
        await Expect(Page.Locator(".join-panel__notice")).ToHaveTextAsync("Подключение пока недоступно");
        await Expect(Page.Locator(".join-panel__copy")).ToHaveCountAsync(0);
        await Expect(Page.Locator("[data-play-refresh]")).ToBeFocusedAsync();
        await Page.Clock.FastForwardAsync(60_000);
        await Expect(Page.Locator(".join-panel__state")).ToHaveTextAsync("Сервер отвечает");
        await Expect(Page.Locator(".join-panel__facts dd").First).ToHaveTextAsync("de_new");
        await Expect(Page.Locator(".join-panel__copy")).ToHaveCountAsync(1);
        responses.Should().BeEmpty();
    }

    [BrowserFact]
    public async Task Player_status_network_failure_clears_current_facts_but_keeps_connection_and_recovers()
    {
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory();
        factory.StartServer();
        await InstallPlayClockAsync(factory);
        var body = await Page.ContentAsync();
        var requests = 0;
        await Page.RouteAsync("**/play", async route =>
        {
            requests++;
            await route.FulfillAsync(requests == 1
                ? new() { Status = 503, ContentType = "text/plain", Body = "fixture error" }
                : new() { ContentType = "text/html", Body = body });
        });
        await Page.Clock.FastForwardAsync(60_000);
        await Expect(Page.Locator(".join-panel__state")).ToHaveTextAsync("Свежий статус не подтвержден");
        await Expect(Page.Locator(".join-panel__facts dd").First).ToHaveTextAsync("Нет свежих данных");
        await Expect(Page.Locator("[data-play-warning]")).ToBeVisibleAsync();
        await Expect(Page.Locator(".join-panel__command")).ToHaveTextAsync("connect play.example.test:27015");
        await Page.Clock.FastForwardAsync(60_000);
        await Expect(Page.Locator(".join-panel__state")).ToHaveTextAsync("Сервер отвечает");
        await Expect(Page.Locator("[data-play-warning]")).ToBeHiddenAsync();
        requests.Should().Be(2);
    }

    [BrowserFact]
    public async Task Player_status_hidden_tab_stops_reads_and_resume_downgrades_expired_observation_before_refresh()
    {
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory();
        factory.StartServer();
        await InstallPlayClockAsync(factory);
        var body = await Page.ContentAsync();
        var requests = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Page.RouteAsync("**/play", async route =>
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
        await Expect(Page.Locator(".join-panel__state")).ToHaveTextAsync("Свежий статус не подтвержден");
        await Expect(Page.Locator(".join-panel__facts dd").First).ToHaveTextAsync("Нет свежих данных");
        release.SetResult();
        await Expect(Page.Locator(".join-panel__state")).ToHaveTextAsync("Сервер отвечает");
        requests.Should().Be(1);
    }

    [BrowserFact]
    public async Task Player_status_timeout_overlap_and_page_cache_lifecycle_are_bounded()
    {
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory();
        factory.StartServer();
        await InstallPlayClockAsync(factory);
        var requests = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Page.RouteAsync("**/play", async route =>
        {
            requests++;
            entered.TrySetResult();
            await release.Task;
            await route.AbortAsync();
        });
        await Page.Locator("[data-play-refresh]").ClickAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Page.Locator("[data-play-refresh]").DispatchEventAsync("click");
        await Page.Clock.RunForAsync(9_000);
        await Expect(Page.Locator(".join-panel__state")).ToHaveTextAsync("Свежий статус не подтвержден");
        requests.Should().Be(1);
        release.SetResult();
        await Page.EvaluateAsync("window.dispatchEvent(new Event('pagehide'))");
        await Page.Clock.RunForAsync(120_000);
        requests.Should().Be(1);
        await Page.EvaluateAsync("window.dispatchEvent(new Event('pageshow')); window.dispatchEvent(new Event('pageshow'))");
        await Expect(Page.Locator("#play-refresh-status")).ToContainTextAsync("Не удалось обновить");
        requests.Should().Be(2);
        await Page.Clock.RunForAsync(5_000);
        requests.Should().Be(2);
    }

    [BrowserFact]
    public async Task Player_status_rejects_missing_oversized_non_html_and_unsafe_refreshes()
    {
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory();
        factory.StartServer();
        await InstallPlayClockAsync(factory);
        var body = await Page.ContentAsync();
        var responses = new Queue<RouteFulfillOptions>([
            new() { ContentType = "text/html", Body = "<html>old page without status block</html>" },
            new() { ContentType = "text/html", Body = body + new string('x', 131_072) },
            new() { ContentType = "application/json", Body = "{}" },
            new() { ContentType = "text/html", Body = body.Replace("id=\"join-title\"", "id=\"join-title\" onclick=\"window.injected=true\"", StringComparison.Ordinal) }
        ]);
        await Page.RouteAsync("**/play", async route => await route.FulfillAsync(responses.Dequeue()));
        for (var i = 0; i < 4; i++)
        {
            await Page.Clock.RunForAsync(5_000);
            await Page.Locator("[data-play-refresh]").ClickAsync();
            await Expect(Page.Locator("#play-refresh-status")).ToContainTextAsync("Не удалось обновить");
            await Expect(Page.Locator(".join-panel__state")).ToHaveTextAsync("Свежий статус не подтвержден");
        }
        (await Page.EvaluateAsync<bool>("window.injected === true")).Should().BeFalse();
    }

    [BrowserFact]
    public async Task Player_status_without_javascript_keeps_server_rendering_and_manual_reload()
    {
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory();
        factory.StartServer();
        await using var context = await Browser.NewContextAsync(new() { JavaScriptEnabled = false, ViewportSize = new() { Width = 320, Height = 740 } });
        var page = await context.NewPageAsync();
        await page.GotoAsync(new Uri(factory.ClientOptions.BaseAddress, "/play").AbsoluteUri);
        await Expect(page.Locator(".join-panel__state")).ToHaveTextAsync("Сервер отвечает");
        await page.Locator("[data-play-refresh]").ClickAsync();
        await Expect(page.Locator(".join-panel__facts dd").First).ToHaveTextAsync("de_dust2");
        (await page.Locator("[data-play-refresh]").GetAttributeAsync("href")).Should().Be("/play");
    }

    [BrowserFact]
    public async Task Player_status_hiding_during_refresh_discards_response_and_retains_manual_recovery()
    {
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory();
        factory.StartServer();
        await InstallPlayClockAsync(factory);
        var body = await PlayBodyAsync(PlayServer("offline"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        await Page.RouteAsync("**/play", async route =>
        {
            requests++;
            entered.TrySetResult();
            await release.Task;
            await route.FulfillAsync(new() { ContentType = "text/html", Body = body });
        });
        await Page.Locator("[data-play-refresh]").ClickAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await SetLeaderboardHiddenAsync(true);
        release.SetResult();
        await Page.Clock.RunForAsync(10_000);
        await Expect(Page.Locator(".join-panel__state")).ToHaveTextAsync("Сервер отвечает");
        await SetLeaderboardHiddenAsync(false);
        await Page.Locator("[data-play-refresh]").ClickAsync();
        await Expect(Page.Locator(".join-panel__state")).ToHaveTextAsync("Сервер не отвечает");
        requests.Should().Be(2);
    }

    private static PublicServerJoinResponse PlayServer(string state) => new(
        "GoldSrcOps", "play.example.test", 27015, state, "de_new", 2, 20, DateTimeOffset.UtcNow);

    private static async Task<string> PlayBodyAsync(PublicServerJoinResponse? server, HttpStatusCode? failure = null)
    {
        await using var factory = new PublicDashboardWebApplicationFactory(serverJoinResponse: server, serverJoinFailureStatus: failure);
        using var client = factory.CreateClient();
        return await client.GetStringAsync("/play");
    }

    private async Task InstallPlayClockAsync(BrowserTokenBoundaryWebApplicationFactory factory)
    {
        var now = DateTime.UtcNow;
        await Page.Clock.InstallAsync(new() { TimeDate = now });
        await Page.GotoAsync(new Uri(factory.ClientOptions.BaseAddress, "/play").AbsoluteUri);
        await Page.Clock.PauseAtAsync(now.AddSeconds(10));
    }
}
