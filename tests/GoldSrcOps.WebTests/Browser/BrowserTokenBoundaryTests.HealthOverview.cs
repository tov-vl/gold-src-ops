using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using GoldSrcOps.Contracts.Alerts;
using GoldSrcOps.Contracts.ProviderDelivery;
using GoldSrcOps.Web.Services;
using Microsoft.Playwright;

namespace GoldSrcOps.WebTests.Browser;

public sealed partial class BrowserTokenBoundaryTests
{
    [BrowserFact]
    public async Task Health_overview_is_static_token_free_independent_and_mobile_readable()
    {
        using var handler = new OverviewFixtureHandler();
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory(gameHostHealthHandler: handler,
            credentialExpiryHandler: handler, archiveHealthHandler: handler, pollingHealthHandler: handler, incidentDeliveryHealthClient: handler, retentionHealthHandler: handler);
        factory.StartServer();
        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            JavaScriptEnabled = false,
            ViewportSize = new ViewportSize { Width = 1440, Height = 1000 }
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync(new Uri(factory.ClientOptions.BaseAddress, BrowserTokenBoundaryWebApplicationFactory.SignInPath).AbsoluteUri);
        var response = await page.GotoAsync(new Uri(factory.ClientOptions.BaseAddress, OperatorHealthOverview.Path).AbsoluteUri);
        response!.Headers["cache-control"].Split(',').Select(static value => value.Trim()).Should().Contain("no-store");
        await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Operations health", Exact = true })).ToBeVisibleAsync();
        await Expect(page.Locator(".operations-card")).ToHaveCountAsync(7);
        await Expect(page.Locator(".operations-card[data-source='archive'] [data-rule-state='Firing']")).ToHaveTextAsync("Firing");
        await Expect(page.GetByText("Recorded minute reached (conservative)", new PageGetByTextOptions { Exact = true })).ToBeVisibleAsync();
        await Expect(page.Locator("[data-source='incident-outbox']").GetByText("Disabled", new LocatorGetByTextOptions { Exact = true })).ToBeVisibleAsync();
        await Expect(page.Locator("[data-source='incident-receiver']").GetByText("Catch-up / sandbox", new LocatorGetByTextOptions { Exact = true })).ToBeVisibleAsync();
        handler.Reads.Should().Equal(1, 1, 1, 1, 1, 1, 1);
        handler.Bearers.Where(static value => value is not null).Should().HaveCount(5)
            .And.OnlyContain(static value => value == BrowserTokenBoundaryWebApplicationFactory.AccessTokenSentinel);
        AssertTokenFree(await page.ContentAsync());
        (await page.ContentAsync()).Should().NotContain("private-sentinel");
        if (Environment.GetEnvironmentVariable(ScreenshotDirectoryVariable) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(directory, "operations-health-desktop.png"), FullPage = true });
        }

        await page.SetViewportSizeAsync(390, 900);
        (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > document.documentElement.clientWidth")).Should().BeFalse();
        handler.ArchiveUnavailable = true;
        handler.ReceiverUnavailable = true;
        await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Refresh", Exact = true }).ClickAsync();
        await Expect(page.Locator(".operations-card[data-source='archive'] [data-source-state='Unknown']")).ToHaveTextAsync("Unknown");
        await Expect(page.Locator(".operations-card[data-source='polling'] [data-source-state='Available']")).ToHaveTextAsync("Available");
        await Expect(page.Locator("[data-source='polling']").GetByText("Running", new LocatorGetByTextOptions { Exact = true })).ToBeVisibleAsync();
        await Expect(page.Locator("[data-source='incident-receiver'] [data-source-state='Unknown']")).ToHaveTextAsync("Unknown");
        await Expect(page.Locator("[data-source='incident-outbox'] [data-source-state='Available']")).ToHaveTextAsync("Available");
        handler.Reads.Should().Equal(2, 2, 2, 2, 2, 2, 2);
        (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > document.documentElement.clientWidth")).Should().BeFalse();
        AssertTokenFree(await page.ContentAsync());
        if (Environment.GetEnvironmentVariable(ScreenshotDirectoryVariable) is { Length: > 0 } mobileDirectory)
        {
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(mobileDirectory, "operations-health-mobile-partial.png"), FullPage = true });
        }

        await page.Locator(".operations-card[data-source='polling']").GetByRole(AriaRole.Link).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Polling health", Exact = true })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Operations health", Exact = true }).ClickAsync();
        await Expect(page.Locator(".operations-card")).ToHaveCountAsync(7);
        AssertTokenFree(await page.ContentAsync());

        handler.IsReceiverConfigured = false;
        var previousReads = handler.Reads[5];
        await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Refresh", Exact = true }).ClickAsync();
        await Expect(page.Locator("[data-source='incident-receiver'] [data-source-state='Disabled']")).ToHaveTextAsync("Disabled");
        handler.Reads[5].Should().Be(previousReads);
        await page.Locator("[data-source='incident-outbox']").GetByRole(AriaRole.Link).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Alert delivery", Exact = true })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Operations health", Exact = true }).ClickAsync();
        await page.Locator("[data-source='incident-receiver']").GetByRole(AriaRole.Link).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Provider delivery", Exact = true })).ToBeVisibleAsync();
        AssertTokenFree(await page.ContentAsync());
    }

    private sealed class OverviewFixtureHandler : HttpMessageHandler, IIncidentDeliveryHealthClient
    {
        public bool ArchiveUnavailable { get; set; }
        public int[] Reads { get; } = new int[7];
        public string?[] Bearers { get; } = new string?[7];
        public bool IsReceiverConfigured { get; set; } = true;
        public bool ReceiverUnavailable { get; set; }
        public bool RetentionUnavailable { get; set; }

        public Task<AlertDeliveryStatusResponse> ReadOutboxAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Reads[4]);
            return Task.FromResult(OperatorHealthOverviewFixtures.Outbox(DateTimeOffset.UtcNow));
        }

        public Task<ProviderDeliveryStatusResponse> ReadReceiverAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Reads[5]);
            return ReceiverUnavailable ? Task.FromException<ProviderDeliveryStatusResponse>(new HttpRequestException("private-sentinel")) :
                Task.FromResult(OperatorHealthOverviewFixtures.Receiver(DateTimeOffset.UtcNow));
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var index = request.RequestUri!.AbsolutePath switch
            {
                "/api/operator/game-host-health" => 0,
                "/api/operator/archive-health" => 1,
                "/api/operator/polling-health" => 2,
                "/api/operator/credential-expiry" => 3,
                "/api/operator/retention-health" => 6,
                _ => throw new InvalidOperationException("Unexpected overview source")
            };
            Interlocked.Increment(ref Reads[index]);
            Bearers[index] = request.Headers.Authorization?.Parameter;
            var now = DateTimeOffset.UtcNow;
            object payload = index switch
            {
                0 => OperatorHealthOverviewFixtures.GameHost(now),
                1 => OperatorHealthOverviewFixtures.Archive(now),
                2 => OperatorHealthOverviewFixtures.Polling(now),
                6 => RetentionHealthFixture.Valid(now),
                _ => OperatorHealthOverviewFixtures.Credentials(now)
            };
            return Task.FromResult(index == 1 && ArchiveUnavailable || index == 6 && RetentionUnavailable ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) :
                new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(payload) });
        }
    }
}
