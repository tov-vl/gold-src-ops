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
    public async Task Archive_health_refresh_is_static_token_free_and_mobile_readable()
    {
        using var handler = new ArchiveFixtureHandler();
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory(archiveHealthHandler: handler);
        factory.StartServer();
        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            JavaScriptEnabled = false,
            ViewportSize = new ViewportSize { Width = 1440, Height = 1000 }
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync(new Uri(factory.ClientOptions.BaseAddress, BrowserTokenBoundaryWebApplicationFactory.SignInPath).AbsoluteUri);
        var response = await page.GotoAsync(new Uri(factory.ClientOptions.BaseAddress, ArchiveHealthView.Path).AbsoluteUri);
        response!.Headers["cache-control"].Split(',').Select(static value => value.Trim()).Should().Contain("no-store");
        await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Archive health", Exact = true })).ToBeVisibleAsync();
        await Expect(page.Locator("[data-observation-state='Available']")).ToHaveTextAsync("Available");
        await Expect(page.Locator(".archive-warning")).ToHaveCountAsync(4);
        await Expect(page.Locator("[data-rule-state='Firing']")).ToHaveTextAsync("Firing");
        await Expect(page.GetByText("Manual runs and reruns do not refresh", new PageGetByTextOptions { Exact = false })).ToBeVisibleAsync();
        AssertTokenFree(await page.ContentAsync());
        handler.Bearer.Should().Be(BrowserTokenBoundaryWebApplicationFactory.AccessTokenSentinel);
        if (Environment.GetEnvironmentVariable(ScreenshotDirectoryVariable) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(directory, "archive-health-desktop.png"), FullPage = true });
        }

        await page.SetViewportSizeAsync(390, 900);
        (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > document.documentElement.clientWidth")).Should().BeFalse();
        handler.Unavailable = true;
        var before = handler.Reads;
        await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Refresh", Exact = true }).ClickAsync();
        await Expect(page.Locator("[data-observation-state='Unknown']")).ToHaveTextAsync("Unknown");
        await Expect(page.Locator("[data-rule-state='Unknown']")).ToHaveCountAsync(4);
        handler.Reads.Should().Be(before + 1);
        AssertTokenFree(await page.ContentAsync());
        if (Environment.GetEnvironmentVariable(ScreenshotDirectoryVariable) is { Length: > 0 } mobileDirectory)
        {
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(mobileDirectory, "archive-health-mobile-unknown.png"), FullPage = true });
        }
    }

    private sealed class ArchiveFixtureHandler : HttpMessageHandler
    {
        public bool Unavailable { get; set; }
        public string? Bearer { get; private set; }
        public int Reads { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Reads++;
            Bearer = request.Headers.Authorization?.Parameter;
            request.RequestUri!.AbsolutePath.Should().Be("/api/operator/archive-health");
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(Unavailable ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new ArchiveHealthResponse(now, "Available", now.AddSeconds(-600), HasScheduledSuccess: true, now.AddHours(-4), 14400, "Available", now,
                    [.. ArchiveHealthView.Definitions.Select(static item => new ArchiveWarningResponse(item.Name, item.Name is "ArchiveScheduleDelayed" ? "Firing" : "Inactive"))]))
            });
        }
    }
}
