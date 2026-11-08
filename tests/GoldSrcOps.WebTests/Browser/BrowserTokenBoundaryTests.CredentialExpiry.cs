using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using GoldSrcOps.Contracts.Monitoring;
using Microsoft.Playwright;

namespace GoldSrcOps.WebTests.Browser;

public sealed partial class BrowserTokenBoundaryTests
{
    [BrowserFact]
    public async Task Credential_expiry_is_Operator_only_static_token_free_and_mobile_readable()
    {
        using var handler = new ExpiryFixtureHandler();
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory(credentialExpiryHandler: handler);
        factory.StartServer();
        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            JavaScriptEnabled = false,
            ViewportSize = new ViewportSize { Width = 1440, Height = 1000 }
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync(new Uri(factory.ClientOptions.BaseAddress, BrowserTokenBoundaryWebApplicationFactory.SignInPath).AbsoluteUri);
        var response = await page.GotoAsync(new Uri(factory.ClientOptions.BaseAddress, "/operator/credential-expiry").AbsoluteUri);
        response!.Headers["cache-control"].Split(',').Select(static value => value.Trim()).Should().Contain("no-store");
        await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Credential expiry", Exact = true })).ToBeVisibleAsync();
        await Expect(page.Locator(".expiry-card")).ToHaveCountAsync(4);
        await Expect(page.Locator(".expiry-state[data-state='Critical']")).ToHaveTextAsync("Due within 7 days");
        await Expect(page.GetByText("Precision: minute", new PageGetByTextOptions { Exact = false })).ToBeVisibleAsync();
        await Expect(page.GetByText("2000-01-01 12:19 UTC", new PageGetByTextOptions { Exact = true })).ToBeVisibleAsync();
        await Expect(page.Locator(".expiry-state[data-state='Expired']")).ToHaveTextAsync("Recorded minute reached (conservative)");
        (await page.ContentAsync()).Should().NotContain("2000-01-01 12:19:00 UTC");
        await Expect(page.GetByText("not live provider checks", new PageGetByTextOptions { Exact = false })).ToBeVisibleAsync();
        AssertTokenFree(await page.ContentAsync());
        handler.Bearer.Should().Be(BrowserTokenBoundaryWebApplicationFactory.AccessTokenSentinel);
        if (Environment.GetEnvironmentVariable(ScreenshotDirectoryVariable) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(directory, "credential-expiry-desktop.png"), FullPage = true });
        }

        await page.SetViewportSizeAsync(390, 900);
        (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > document.documentElement.clientWidth")).Should().BeFalse();
        handler.Unavailable = true;
        await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Refresh", Exact = true }).ClickAsync();
        await Expect(page.Locator(".expiry-state[data-state='Unknown']")).ToHaveCountAsync(4);
        AssertTokenFree(await page.ContentAsync());
    }

    private sealed class ExpiryFixtureHandler : HttpMessageHandler
    {
        public bool Unavailable { get; set; }
        public string? Bearer { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bearer = request.Headers.Authorization?.Parameter;
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(Unavailable ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new CredentialExpiryResponse(now, [
                    new CredentialExpiryEntryResponse("grafana-metrics", "Critical", now.AddDays(7), now, 7),
                    new CredentialExpiryEntryResponse("archive-reader", "Expired", ExpiresAtUtc: null, now, 0, "2000-01-01T12:19Z")
                ]))
            });
        }
    }
}
