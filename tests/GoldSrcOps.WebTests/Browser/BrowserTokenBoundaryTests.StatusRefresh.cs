using System.Net;
using AwesomeAssertions;
using GoldSrcOps.Contracts.Monitoring;
using GoldSrcOps.WebTests.Pages;
using Microsoft.Playwright;

namespace GoldSrcOps.WebTests.Browser;

public sealed partial class BrowserTokenBoundaryTests
{
    [BrowserFact]
    public async Task Dashboard_manual_refresh_preserves_language_range_document_scroll_and_cookie_boundary()
    {
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory();
        factory.StartServer();
        await Page.GotoAsync(new Uri(factory.ClientOptions.BaseAddress, BrowserTokenBoundaryWebApplicationFactory.SignInPath).AbsoluteUri);
        (await Context.CookiesAsync()).Should().Contain(cookie => cookie.Name == BrowserTokenBoundaryWebApplicationFactory.AuthenticationCookieName);
        foreach (var language in new[] { "ru", "en" })
        {
            var path = string.Equals(language, "en", StringComparison.Ordinal) ? "/?range=7d&lang=en" : "/?range=7d";
            await Page.GotoAsync(new Uri(factory.ClientOptions.BaseAddress, path).AbsoluteUri);
            var body = await DashboardBodyAsync(language, PlayServer("online") with { Host = "new.example.test", Name = "<script>window.injected=true</script>" });
            var requests = new List<IRequest>();
            await Page.RouteAsync("**/?range=7d*", async route =>
            {
                requests.Add(route.Request);
                await route.FulfillAsync(new() { ContentType = "text/html", Body = body });
            });
            await Page.EvaluateAsync("window.__documentMarker = 42; window.__copyNode = document.querySelector('.public-server__copy')");
            await Page.Locator("[data-status-refresh]").FocusAsync();
            await Page.EvaluateAsync("window.scrollTo(0, 200)");
            var scroll = await Page.EvaluateAsync<double>("window.scrollY");
            await Page.Locator("[data-status-refresh]").DispatchEventAsync("click");
            await Expect(Page.Locator("#status-refresh-message")).ToHaveTextAsync(string.Equals(language, "en", StringComparison.Ordinal) ? "Check complete." : "Проверка завершена.");
            await Expect(Page.Locator("[data-status-refresh]")).ToBeFocusedAsync();
            await Expect(Page.Locator("#public-server-title")).ToHaveTextAsync("<script>window.injected=true</script>");
            (await Page.EvaluateAsync<int>("window.__documentMarker")).Should().Be(42);
            (await Page.EvaluateAsync<double>("window.scrollY")).Should().Be(scroll);
            (await Page.EvaluateAsync<bool>("window.__copyNode === document.querySelector('.public-server__copy')")).Should().BeTrue();
            (await Page.EvaluateAsync<bool>("window.injected === true")).Should().BeFalse();
            await Page.EvaluateAsync("navigator.clipboard.writeText = async value => { window.__copiedCommand = value; }");
            await Page.Locator(".public-server__copy").ClickAsync();
            await Expect(Page.Locator("#public-server-copy-status")).ToHaveTextAsync(string.Equals(language, "en", StringComparison.Ordinal) ? "Connection command copied." : "Команда подключения скопирована.");
            (await Page.EvaluateAsync<string>("window.__copiedCommand")).Should().Be("connect new.example.test:27015");
            await AssertAnonymousRefreshAsync(requests.Should().ContainSingle().Which);
            AssertTokenFree(await Page.ContentAsync());
            await AssertBrowserStorageIsEmptyAsync();
            await Page.UnrouteAsync("**/?range=7d*");
        }
    }

    [BrowserFact]
    public async Task Dashboard_automatic_refresh_handles_partial_failure_configuration_removal_and_recovery()
    {
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory();
        factory.StartServer();
        await InstallDashboardClockAsync(factory);
        var responses = new Queue<string>([
            await DashboardBodyAsync("ru", PlayServer("offline")),
            await DashboardBodyAsync("ru", null, HttpStatusCode.ServiceUnavailable),
            await DashboardBodyAsync("ru", null, HttpStatusCode.NotFound),
            await DashboardBodyAsync("ru", PlayServer("online"), statusUnavailable: true),
            await DashboardBodyAsync("ru", PlayServer("online"))
        ]);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Page.RouteAsync("**/?range=7d", async route =>
        {
            await release.Task;
            await route.FulfillAsync(new() { ContentType = "text/html", Body = responses.Dequeue() });
        });
        await Page.Locator(".public-server__copy").FocusAsync();
        await Page.Clock.FastForwardAsync(60_000);
        release.SetResult();
        await Expect(Page.Locator("[data-status-server]")).ToHaveAttributeAsync("data-state", "offline");
        await Expect(Page.Locator(".public-server__copy")).ToBeFocusedAsync();
        await Expect(Page.Locator(".public-server__facts dd").First).ToHaveTextAsync("Нет свежих данных");
        await Page.Clock.FastForwardAsync(60_000);
        await Expect(Page.Locator("#status-refresh-message")).ToContainTextAsync("Не удалось обновить");
        await Expect(Page.Locator("[data-status-server]")).ToHaveAttributeAsync("data-state", "unknown");
        await Expect(Page.Locator(".public-server__copy")).ToBeFocusedAsync();
        await Page.Clock.FastForwardAsync(60_000);
        await Expect(Page.Locator("[data-status-server]")).ToHaveAttributeAsync("data-state", "unavailable");
        await Expect(Page.Locator(".public-server__copy")).ToHaveCountAsync(0);
        await Expect(Page.Locator("[data-status-refresh]")).ToBeFocusedAsync();
        await Page.Clock.FastForwardAsync(60_000);
        await Expect(Page.Locator("[data-status-fleet]")).ToHaveAttributeAsync("data-state", "unavailable");
        await Expect(Page.Locator("[data-status-server]")).ToHaveAttributeAsync("data-state", "online");
        await Expect(Page.Locator(".history-bar")).ToHaveCountAsync(28);
        await Page.Clock.FastForwardAsync(60_000);
        await Expect(Page.Locator("[data-status-fleet]")).ToHaveAttributeAsync("data-state", "operational");
        await Expect(Page.Locator("#status-refresh-message")).ToHaveTextAsync("Проверка завершена.");
        responses.Should().BeEmpty();
    }

    [BrowserFact]
    public async Task Dashboard_failed_or_unsafe_refresh_keeps_history_and_clears_current_claims_then_recovers()
    {
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory();
        factory.StartServer();
        await InstallDashboardClockAsync(factory);
        var body = await Page.ContentAsync();
        var responses = new Queue<RouteFulfillOptions>([
            new() { Status = 503, Body = "unavailable" },
            new() { ContentType = "text/html", Body = "<html>missing snapshot</html>" },
            new() { ContentType = "text/html", Body = body + new string('x', 131_072) },
            new() { ContentType = "application/json", Body = "{}" },
            new() { ContentType = "text/html", Body = body.Replace("data-window=\"7d\"", "data-window=\"24h\"", StringComparison.Ordinal) },
            new() { ContentType = "text/html", Body = body.Replace("class=\"status-page\" lang=\"ru\"", "class=\"status-page\" lang=\"en\"", StringComparison.Ordinal) },
            new() { ContentType = "text/html", Body = body.Replace("id=\"public-server-title\"", "id=\"public-server-title\" onclick=\"window.injected=true\"", StringComparison.Ordinal) },
            new() { ContentType = "text/html", Body = body.Replace("steam://connect/play.example.test:27015", "javascript:alert(1)", StringComparison.Ordinal) }
        ]);
        await Page.RouteAsync("**/?range=7d", async route => await route.FulfillAsync(responses.Dequeue()));
        while (responses.Count > 0)
        {
            await Page.Clock.RunForAsync(5_000);
            await Page.Locator("[data-status-refresh]").ClickAsync();
            await Expect(Page.Locator("#status-refresh-message")).ToContainTextAsync("Не удалось обновить");
            await Expect(Page.Locator("[data-status-fleet]")).ToHaveAttributeAsync("data-state", "unknown");
            await Expect(Page.Locator(".public-server__facts dd").First).ToHaveTextAsync("Нет свежих данных");
            await Expect(Page.Locator(".history-bar")).ToHaveCountAsync(28);
        }
        (await Page.EvaluateAsync<bool>("window.injected === true")).Should().BeFalse();
        await Page.UnrouteAsync("**/?range=7d");
        await Page.Clock.RunForAsync(5_000);
        await Page.Locator("[data-status-refresh]").ClickAsync();
        await Expect(Page.Locator("#status-refresh-message")).ToHaveTextAsync("Проверка завершена.");
        await Expect(Page.Locator("[data-status-fleet]")).ToHaveAttributeAsync("data-state", "operational");
        await Expect(Page.Locator(".public-server__facts dd").First).ToHaveTextAsync("de_dust2");
    }

    [BrowserFact]
    public async Task Dashboard_hidden_tab_pauses_reads_and_expires_claims_before_resuming()
    {
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory();
        factory.StartServer();
        await InstallDashboardClockAsync(factory);
        var body = await Page.ContentAsync();
        var requests = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Page.RouteAsync("**/?range=7d", async route =>
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
        await Expect(Page.Locator("[data-status-fleet]")).ToHaveAttributeAsync("data-state", "unknown");
        await Expect(Page.Locator("[data-status-server]")).ToHaveAttributeAsync("data-state", "unknown");
        await Expect(Page.Locator("[data-status-fleet] [data-status-age]")).ToContainTextAsync("сек.");
        await SetLeaderboardHiddenAsync(true);
        release.SetResult();
        await Page.Clock.RunForAsync(10_000);
        await Expect(Page.Locator("[data-status-server]")).ToHaveAttributeAsync("data-state", "unknown");
        await SetLeaderboardHiddenAsync(false);
        await Page.Locator("[data-status-refresh]").ClickAsync();
        await Expect(Page.Locator("[data-status-server]")).ToHaveAttributeAsync("data-state", "online");
        requests.Should().Be(2);
    }

    [BrowserFact]
    public async Task Dashboard_timeout_overlap_and_page_cache_lifecycle_are_bounded()
    {
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory();
        factory.StartServer();
        await InstallDashboardClockAsync(factory);
        var requests = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Page.RouteAsync("**/?range=7d", async route =>
        {
            requests++;
            entered.TrySetResult();
            await release.Task;
            await route.AbortAsync();
        });
        await Page.Locator("[data-status-refresh]").ClickAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Page.Locator("[data-status-refresh]").DispatchEventAsync("click");
        await Page.Clock.RunForAsync(9_000);
        await Expect(Page.Locator("#status-refresh-message")).ToContainTextAsync("Не удалось обновить");
        requests.Should().Be(1);
        release.SetResult();
        await Page.EvaluateAsync("window.dispatchEvent(new Event('pagehide'))");
        await Page.Clock.RunForAsync(120_000);
        requests.Should().Be(1);
        await Page.EvaluateAsync("window.dispatchEvent(new Event('pageshow')); window.dispatchEvent(new Event('pageshow'))");
        await Expect(Page.Locator("[data-status-refresh]")).Not.ToHaveAttributeAsync("aria-disabled", "true");
        requests.Should().Be(2);
        await Page.Clock.RunForAsync(5_000);
        requests.Should().Be(2);
    }

    [BrowserFact]
    public async Task Dashboard_without_javascript_refreshes_both_languages_and_preserves_history_window()
    {
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory();
        factory.StartServer();
        await using var context = await Browser.NewContextAsync(new() { JavaScriptEnabled = false, ViewportSize = new() { Width = 320, Height = 900 } });
        var page = await context.NewPageAsync();
        foreach (var language in new[] { "ru", "en" })
        {
            var path = "/?range=7d" + (string.Equals(language, "en", StringComparison.Ordinal) ? "&lang=en" : string.Empty);
            await page.GotoAsync(new Uri(factory.ClientOptions.BaseAddress, path).AbsoluteUri);
            await page.Locator("[data-status-refresh]").ClickAsync();
            await Expect(page).ToHaveURLAsync(new Uri(factory.ClientOptions.BaseAddress, path).AbsoluteUri);
            await Expect(page.Locator(".status-page")).ToHaveAttributeAsync("lang", language);
            await Expect(page.Locator(".history-bar")).ToHaveCountAsync(28);
            await Expect(page.Locator("[data-status-server]")).ToHaveAttributeAsync("data-state", "online");
        }
    }

    private static async Task<string> DashboardBodyAsync(string language, PublicServerJoinResponse? server,
        HttpStatusCode? failure = null, bool statusUnavailable = false)
    {
        await using var factory = new PublicDashboardWebApplicationFactory(statusUnavailable: statusUnavailable,
            serverJoinResponse: server, serverJoinFailureStatus: failure);
        using var client = factory.CreateClient();
        return await client.GetStringAsync("/?range=7d&lang=" + language);
    }

    private async Task InstallDashboardClockAsync(BrowserTokenBoundaryWebApplicationFactory factory)
    {
        var now = DateTime.UtcNow;
        await Page.Clock.InstallAsync(new() { TimeDate = now });
        await Page.GotoAsync(new Uri(factory.ClientOptions.BaseAddress, "/?range=7d").AbsoluteUri);
        await Page.Clock.PauseAtAsync(now.AddSeconds(10));
    }
}
