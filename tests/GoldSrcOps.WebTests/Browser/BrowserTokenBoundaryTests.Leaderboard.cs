using System.Net;
using AwesomeAssertions;
using GoldSrcOps.Contracts.Monitoring;
using Microsoft.Playwright;

namespace GoldSrcOps.WebTests.Browser;

public sealed partial class BrowserTokenBoundaryTests
{
    [BrowserFact]
    public async Task Public_leaderboard_fits_desktop_and_mobile_and_links_to_join_without_browser_api_requests()
    {
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory();
        factory.StartServer();
        foreach (var viewport in new[] { new ViewportSize { Width = 1440, Height = 900 }, new ViewportSize { Width = 390, Height = 844 }, new ViewportSize { Width = 320, Height = 740 } })
        {
            var browserApiRequests = 0;
            void ObserveRequest(object? sender, IRequest request)
            {
                if (request.Url.Contains("/api/", StringComparison.Ordinal))
                {
                    browserApiRequests++;
                }
            }
            Page.Request += ObserveRequest;
            try
            {
                await Page.SetViewportSizeAsync(viewport.Width, viewport.Height);
                await Page.GotoAsync(factory.ClientOptions.BaseAddress.AbsoluteUri);
                await Page.Locator(".primary-nav a[href='/leaderboard']").ClickAsync();
                await Expect(Page.Locator(".leaderboard-state")).ToHaveTextAsync("Свежий снимок");
                await Expect(Page.Locator(".leaderboard-table tbody tr")).ToHaveCountAsync(2);
                await Expect(Page.Locator(".leaderboard-table tbody th").First).ToHaveTextAsync("Игрок <script>alert(1)</script>");
                (await Page.EvaluateAsync<bool>("typeof window.nameInjected !== 'undefined'")).Should().BeFalse();
                (await Page.EvaluateAsync<bool>("document.documentElement.scrollWidth > document.documentElement.clientWidth")).Should().BeFalse();
                AssertTokenFree(await Page.ContentAsync());
                await AssertBrowserStorageIsEmptyAsync();
                browserApiRequests.Should().Be(0);
                await CaptureScreenshotIfRequestedAsync("leaderboard", viewport);
                await Page.Locator(".leaderboard-join").ClickAsync();
                await Expect(Page.Locator(".play-page")).ToBeVisibleAsync();
            }
            finally
            {
                Page.Request -= ObserveRequest;
            }
        }
    }

    [BrowserFact]
    public async Task Leaderboard_stale_empty_and_unavailable_states_fit_mobile()
    {
        await Page.SetViewportSizeAsync(320, 740);
        foreach (var state in new[] { "stale", "empty", "unavailable", "missing" })
        {
            PublicLeaderboardEntryResponse[] entries = string.Equals(state, "stale", StringComparison.Ordinal)
                ? [new(1, new string('W', 31), 4, 1_000_000_000, 1_000_000_000)] : [];
            await using var factory = new BrowserTokenBoundaryWebApplicationFactory(
                leaderboard: new(string.Equals(state, "empty", StringComparison.Ordinal) ? "fresh" : state, DateTimeOffset.UtcNow, entries),
                leaderboardFailure: string.Equals(state, "missing", StringComparison.Ordinal) ? HttpStatusCode.ServiceUnavailable : null);
            factory.StartServer();
            await Page.GotoAsync(new Uri(factory.ClientOptions.BaseAddress, "/leaderboard").AbsoluteUri);
            await Expect(Page.Locator(".leaderboard-join")).ToBeVisibleAsync();
            (await Page.EvaluateAsync<bool>("document.documentElement.scrollWidth > document.documentElement.clientWidth")).Should().BeFalse();
            await CaptureScreenshotIfRequestedAsync($"leaderboard-{state}", new ViewportSize { Width = 320, Height = 740 });
        }
    }
}
