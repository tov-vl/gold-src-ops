using System.Text;
using AwesomeAssertions;
using GoldSrcOps.Web.Services;
using Microsoft.Playwright;

namespace GoldSrcOps.WebTests.Browser;

public sealed partial class BrowserTokenBoundaryTests
{
    [BrowserFact]
    public async Task Complete_report_download_works_without_scripts_and_isolates_sources()
    {
        using var handler = new OverviewFixtureHandler();
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory(gameHostHealthHandler: handler,
            credentialExpiryHandler: handler, archiveHealthHandler: handler, pollingHealthHandler: handler, incidentDeliveryHealthClient: handler, retentionHealthHandler: handler, commandDispatchHealthHandler: handler);
        factory.StartServer();
        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            JavaScriptEnabled = false,
            ViewportSize = new ViewportSize { Width = 1440, Height = 1000 }
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync(new Uri(factory.ClientOptions.BaseAddress, BrowserTokenBoundaryWebApplicationFactory.SignInPath).AbsoluteUri);
        await page.GotoAsync(new Uri(factory.ClientOptions.BaseAddress, OperatorHealthOverview.Path).AbsoluteUri);
        var report = await DownloadCompleteReportAsync(page);
        report.Should().Contain("Credential snapshot: Available").And.Contain("Precision: minute (conservative start)")
            .And.Contain("Incident outbox snapshot: Available").And.Contain("Incident receiver snapshot: Available")
            .And.Contain("Command dispatcher observation: Running").And.Contain("CommandDispatchCycleFailed: Inactive")
            .And.Contain("Retention observation: Running").And.Contain("Retention consecutive failures: 0")
            .And.Contain("SnapshotRetentionCycleFailed: Inactive").And.Contain("Receiver mode: Catch-up / sandbox").And.Contain("Dispatcher: Disabled");
        handler.Reads.Should().Equal(2, 2, 2, 2, 2, 2, 2, 2);
        handler.Bearers.Where(static item => item is not null).Should().HaveCount(6)
            .And.OnlyContain(static item => item == BrowserTokenBoundaryWebApplicationFactory.AccessTokenSentinel);
        handler.ArchiveUnavailable = true;
        handler.ReceiverUnavailable = true;
        handler.RetentionUnavailable = true;
        handler.CommandDispatchUnavailable = true;
        await page.SetViewportSizeAsync(390, 900);
        (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > document.documentElement.clientWidth")).Should().BeFalse();
        report = await DownloadCompleteReportAsync(page);
        report.Should().Contain("Archive observation: Unknown").And.Contain("Incident receiver snapshot: Unknown")
            .And.Contain("Incident outbox snapshot: Available").And.Contain("Credential snapshot: Available")
            .And.Contain("Command dispatcher observation: Unknown").And.Contain("CommandDispatchCycleFailed: Unknown")
            .And.Contain("Retention observation: Unknown").And.Contain("SnapshotRetentionCycleFailed: Unknown");
        handler.IsReceiverConfigured = false;
        report = await DownloadCompleteReportAsync(page);
        report.Should().Contain("Incident receiver snapshot: Disabled").And.Contain("Pending: Not queried");
        handler.Reads.Should().Equal(4, 4, 4, 4, 4, 3, 4, 4);
        AssertTokenFree(await page.ContentAsync());
    }

    private static async Task<string> DownloadCompleteReportAsync(IPage page)
    {
        var download = await page.RunAndWaitForDownloadAsync(async () =>
            await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Download health report", Exact = true }).ClickAsync());
        download.SuggestedFilename.Should().Be("goldsrcops-game-host-health.txt");
        (await download.FailureAsync()).Should().BeNull();
        var bytes = await File.ReadAllBytesAsync(await download.PathAsync());
        bytes.Length.Should().BeLessThan(32768);
        var report = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        report.Should().Contain("GoldSrcOps game-host health report").And.NotContain("private-sentinel");
        AssertTokenFree(report);
        page.Url.Should().EndWith(OperatorHealthOverview.Path);
        return report;
    }
}
