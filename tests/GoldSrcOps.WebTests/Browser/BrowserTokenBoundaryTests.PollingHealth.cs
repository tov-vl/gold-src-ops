using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using GoldSrcOps.Contracts.Monitoring;
using GoldSrcOps.Web.Services;
using Microsoft.Playwright;

namespace GoldSrcOps.WebTests.Browser;

public sealed partial class BrowserTokenBoundaryTests
{
    [BrowserFact]
    public async Task Polling_health_refresh_is_static_token_free_and_mobile_readable()
    {
        using var handler = new PollingFixtureHandler();
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory(pollingHealthHandler: handler);
        factory.StartServer();
        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            JavaScriptEnabled = false,
            ViewportSize = new ViewportSize { Width = 1440, Height = 1000 }
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync(new Uri(factory.ClientOptions.BaseAddress, BrowserTokenBoundaryWebApplicationFactory.SignInPath).AbsoluteUri);
        var response = await page.GotoAsync(new Uri(factory.ClientOptions.BaseAddress, PollingHealthView.Path).AbsoluteUri);
        response!.Headers["cache-control"].Split(',').Select(static value => value.Trim()).Should().Contain("no-store");
        await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Polling health", Exact = true })).ToBeVisibleAsync();
        await Expect(page.Locator("[data-observation-state='Running']")).ToHaveTextAsync("Running");
        await Expect(page.Locator(".polling-warning")).ToHaveCountAsync(3);
        await Expect(page.Locator("[data-rule-state='Firing']")).ToHaveTextAsync("Firing");
        await Expect(page.GetByText("Completed empty passes and failed A2S replies", new PageGetByTextOptions { Exact = false })).ToBeVisibleAsync();
        AssertTokenFree(await page.ContentAsync());
        handler.Bearer.Should().Be(BrowserTokenBoundaryWebApplicationFactory.AccessTokenSentinel);
        if (Environment.GetEnvironmentVariable(ScreenshotDirectoryVariable) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(directory, "polling-health-desktop.png"), FullPage = true });
        }

        await page.SetViewportSizeAsync(390, 900);
        (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > document.documentElement.clientWidth")).Should().BeFalse();
        handler.Unavailable = true;
        var before = handler.Reads;
        await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Refresh", Exact = true }).ClickAsync();
        await Expect(page.Locator("[data-observation-state='Unknown']")).ToHaveTextAsync("Unknown");
        await Expect(page.Locator("[data-rule-state='Unknown']")).ToHaveCountAsync(3);
        handler.Reads.Should().Be(before + 1);
        AssertTokenFree(await page.ContentAsync());
        if (Environment.GetEnvironmentVariable(ScreenshotDirectoryVariable) is { Length: > 0 } mobileDirectory)
        {
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(mobileDirectory, "polling-health-mobile-unknown.png"), FullPage = true });
        }
    }

    private sealed class PollingFixtureHandler : HttpMessageHandler
    {
        public bool Unavailable { get; set; }
        public string? Bearer { get; private set; }
        public int Reads { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Reads++;
            Bearer = request.Headers.Authorization?.Parameter;
            request.RequestUri!.AbsolutePath.Should().Be("/api/operator/polling-health");
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(Unavailable ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new PollingHealthResponse(now, "Running", now, now.AddSeconds(-6), now.AddSeconds(-5), 5, 370, "Available", now,
                    [.. PollingHealthView.Definitions.Select(static item => new PollingWarningResponse(item.Name, item.Name is "PollingCycleFailed" ? "Firing" : "Inactive"))]))
            });
        }
    }
}
