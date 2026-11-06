using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using GoldSrcOps.Contracts.Monitoring;
using Microsoft.Playwright;

namespace GoldSrcOps.WebTests.Browser;

public sealed partial class BrowserTokenBoundaryTests
{
    [BrowserFact]
    public async Task Operator_health_is_server_rendered_token_free_and_readable_without_scripts()
    {
        using var handler = new HealthFixtureHandler();
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory(gameHostHealthHandler: handler);
        factory.StartServer();
        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            JavaScriptEnabled = false,
            ViewportSize = new ViewportSize { Width = 1440, Height = 1000 }
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync(new Uri(factory.ClientOptions.BaseAddress, BrowserTokenBoundaryWebApplicationFactory.SignInPath).AbsoluteUri);
        await page.GotoAsync(new Uri(factory.ClientOptions.BaseAddress, "/operator/game-host-health").AbsoluteUri);
        await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Game-host health", Exact = true })).ToBeVisibleAsync();
        await Expect(page.Locator(".state-firing")).ToContainTextAsync("Player backup");
        AssertTokenFree(await page.ContentAsync());
        handler.Bearer.Should().Be(BrowserTokenBoundaryWebApplicationFactory.AccessTokenSentinel);
        if (Environment.GetEnvironmentVariable(ScreenshotDirectoryVariable) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(directory, "game-host-health-desktop.png"), FullPage = true });
        }

        await page.SetViewportSizeAsync(390, 900);
        (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > document.documentElement.clientWidth")).Should().BeFalse();
        handler.Unavailable = true;
        await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Refresh", Exact = true }).ClickAsync();
        await Expect(page.Locator(".state-unknown")).ToContainTextAsync("No usable health snapshot");
        AssertTokenFree(await page.ContentAsync());
    }

    private sealed class HealthFixtureHandler : HttpMessageHandler
    {
        public bool Unavailable { get; set; }
        public string? Bearer { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri!.AbsolutePath.Should().Be("/api/operator/game-host-health");
            Bearer = request.Headers.Authorization?.Parameter;
            return Task.FromResult(new HttpResponseMessage(Unavailable ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new GameHostHealthResponse(DateTimeOffset.UtcNow,
                    [new GameHostHealthComponentResponse("backup", "Firing", DateTimeOffset.UtcNow),
                     new GameHostHealthComponentResponse("cpu", "Observed", DateTimeOffset.UtcNow)],
                    [new GameHostHealthValueResponse("weeklyAttempt", string.Empty, 9),
                     new GameHostHealthValueResponse("backupSource", string.Empty, 2)]))
            });
        }
    }
}
