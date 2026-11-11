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
        using var archiveHandler = new ArchiveFixtureHandler();
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory(gameHostHealthHandler: handler, archiveHealthHandler: archiveHandler);
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
        await Expect(page.Locator(".health-card.state-firing")).ToContainTextAsync("Player backup");
        await Expect(page.Locator(".warning-row")).ToHaveCountAsync(2);
        await Expect(page.Locator(".warning-row.state-firing")).ToContainTextAsync("PlayerBackupInterventionRequired");
        await Expect(page.Locator(".warning-row.state-pending")).ToContainTextAsync("GameEventAgentMonitorUnavailable");
        await Expect(page.Locator(".warning-row.state-firing .warning-guidance")).ToContainTextAsync("Do not retry an uncertain backup");
        await Expect(page.Locator(".warning-row.state-firing .warning-runbook")).ToHaveAttributeAsync("href",
            "https://github.com/tov-vl/gold-src-ops/blob/main/docs/game-host-warning-triage.md#backup-intervention-required");
        await Expect(page.Locator(".warning-row.state-pending .warning-runbook")).ToHaveAttributeAsync("href",
            "https://github.com/tov-vl/gold-src-ops/blob/main/docs/game-host-warning-triage.md#agent-monitor-unavailable");
        AssertTokenFree(await page.ContentAsync());
        handler.Bearer.Should().Be(BrowserTokenBoundaryWebApplicationFactory.AccessTokenSentinel);
        var download = await page.RunAndWaitForDownloadAsync(async () =>
            await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Download health report", Exact = true }).ClickAsync());
        download.SuggestedFilename.Should().Be("goldsrcops-game-host-health.txt");
        (await download.FailureAsync()).Should().BeNull();
        var report = await File.ReadAllTextAsync(await download.PathAsync());
        report.Should().Contain("GoldSrcOps game-host health report").And.Contain("Snapshot: Available")
            .And.Contain("PlayerBackupInterventionRequired: Firing").And.Contain("Last weekly attempt: Closed: outcome uncertain")
            .And.Contain("Archive observation: Available").And.Contain("Archive warning snapshot: Available")
            .And.Contain("ArchiveScheduleDelayed: Firing").And.Contain("Archive snapshot evaluated:");
        archiveHandler.Reads.Should().Be(1);
        archiveHandler.Bearer.Should().Be(BrowserTokenBoundaryWebApplicationFactory.AccessTokenSentinel);
        AssertTokenFree(report);
        page.Url.Should().EndWith("/operator/game-host-health");
        if (Environment.GetEnvironmentVariable(ScreenshotDirectoryVariable) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(directory, "game-host-health-desktop.png"), FullPage = true });
        }

        await page.SetViewportSizeAsync(390, 900);
        (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > document.documentElement.clientWidth")).Should().BeFalse();
        await Expect(page.Locator(".warning-row.state-firing .warning-guidance")).ToBeVisibleAsync();
        handler.EmptyWarnings = true;
        await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Refresh", Exact = true }).ClickAsync();
        await Expect(page.Locator(".warnings-empty")).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Troubleshooting all 17 warnings and unavailable snapshots", Exact = true })).ToBeVisibleAsync();
        await Expect(page.Locator(".warning-guidance")).ToHaveCountAsync(0);
        handler.WarningState = "Stale";
        await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Refresh", Exact = true }).ClickAsync();
        await Expect(page.Locator(".warnings-stale")).ToBeVisibleAsync();
        await Expect(page.Locator(".warning-runbook")).ToHaveAttributeAsync("href",
            "https://github.com/tov-vl/gold-src-ops/blob/main/docs/game-host-warning-triage.md#stale-snapshot");
        await Expect(page.Locator(".warnings-empty")).ToHaveCountAsync(0);
        handler.WarningState = "Unknown";
        await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Refresh", Exact = true }).ClickAsync();
        await Expect(page.Locator(".warnings-unknown")).ToBeVisibleAsync();
        await Expect(page.Locator(".warning-runbook")).ToHaveAttributeAsync("href",
            "https://github.com/tov-vl/gold-src-ops/blob/main/docs/game-host-warning-triage.md#unknown-snapshot");
        AssertTokenFree(await page.ContentAsync());
        handler.Unavailable = true;
        await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Refresh", Exact = true }).ClickAsync();
        await Expect(page.Locator(".state-unknown")).ToContainTextAsync("No usable health snapshot");
        await Expect(page.Locator(".warning-guidance")).ToContainTextAsync("Refresh once");
        AssertTokenFree(await page.ContentAsync());
    }

    private sealed class HealthFixtureHandler : HttpMessageHandler
    {
        public bool Unavailable { get; set; }
        public bool EmptyWarnings { get; set; }
        public string WarningState { get; set; } = "Available";
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
                     new GameHostHealthValueResponse("backupSource", string.Empty, 2)],
                    new GameHostWarningsResponse(WarningState, DateTimeOffset.UtcNow, EmptyWarnings ? [] :
                    [new GameHostWarningResponse("PlayerBackupInterventionRequired", "player-backup", "Firing", "A backup attempt needs review. Do not retry an uncertain operation."),
                     new GameHostWarningResponse("GameEventAgentMonitorUnavailable", "game-event-agent", "Pending", "Agent state is unknown.")])))
            });
        }
    }
}
