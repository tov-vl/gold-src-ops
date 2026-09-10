using System.Text.RegularExpressions;
using AwesomeAssertions;
using GoldSrcOps.WebTests.Pages;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit;

namespace GoldSrcOps.WebTests.Browser;

public sealed partial class BrowserTokenBoundaryTests : PageTest
{
    private const string ScreenshotDirectoryVariable = "GOLDSRCOPS_UI_SCREENSHOT_DIR";

    [BrowserFact]
    public async Task Authenticated_browser_receives_only_an_opaque_session_key()
    {
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory();
        factory.StartServer();
        var baseAddress = factory.ClientOptions.BaseAddress;

        var listPage = await VisitAsync(BrowserTokenBoundaryWebApplicationFactory.SignInPath);
        var detailPage = await VisitAsync($"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}");
        var historyPage = await VisitAsync(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/history");
        var commandsPage = await VisitAsync(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/commands");
        var sayCommandPage = await VisitAsync(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/commands/new");
        var monitoringPage = await VisitAsync(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/monitoring");
        var settingsPage = await VisitAsync(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/settings");
        var credentialsPage = await VisitAsync(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/credentials");
        var registrationPage = await VisitAsync("/operator/servers/new");
        var incidentsPage = await VisitAsync("/operator/incidents");
        var deadLettersPage = await VisitAsync("/operator/dead-letters");
        var deadLetterDetailPage = await VisitAsync(
            $"/operator/dead-letters/{ReaderWebApplicationFactory.DeadLetterEventId:D}");
        var replayReceiptPage = await VisitAsync(
            $"/operator/replays/{ReaderWebApplicationFactory.ReplayRequestId:D}");

        Page.Url.Should().EndWith($"/operator/replays/{ReaderWebApplicationFactory.ReplayRequestId:D}");
        var stylesheetHrefs = await Page.EvaluateAsync<string[]>(
            "Array.from(document.styleSheets, sheet => sheet.href ?? '')");
        stylesheetHrefs.Should().Contain(
            href => href.Contains("GoldSrcOps.Web.", StringComparison.Ordinal) &&
                    href.EndsWith(".styles.css", StringComparison.Ordinal));
        listPage.Body.Should().Contain(ReaderWebApplicationFactory.ServerName);
        detailPage.Body.Should().Contain("Latest observation");
        historyPage.Body.Should().Contain("Recent observations");
        commandsPage.Body.Should().Contain(ReaderWebApplicationFactory.CommandResultSummary);
        incidentsPage.Body.Should().Contain(ReaderWebApplicationFactory.OpenIncidentReason);
        deadLettersPage.Body.Should().Contain(ReaderWebApplicationFactory.DeadLetterLastError);
        deadLetterDetailPage.Body.Should().Contain("Ordering warning");
        replayReceiptPage.Body.Should().Contain(ReaderWebApplicationFactory.ReplayReason);
        foreach (var page in new[]
                 {
                     listPage,
                     detailPage,
                     historyPage,
                     commandsPage,
                     sayCommandPage,
                     monitoringPage,
                     settingsPage,
                     credentialsPage,
                     registrationPage,
                     incidentsPage,
                     deadLettersPage,
                     deadLetterDetailPage,
                     replayReceiptPage
                 })
        {
            AssertTokenFree(page.Body);
            AssertTokenFree(page.Dom);
            page.Body.Should().NotContain(ReaderWebApplicationFactory.CommandPayloadSentinel);
            page.Dom.Should().NotContain(ReaderWebApplicationFactory.CommandPayloadSentinel);
            page.Body.Should().NotContain(ReaderWebApplicationFactory.DeadLetterPayloadSentinel);
            page.Dom.Should().NotContain(ReaderWebApplicationFactory.DeadLetterPayloadSentinel);
        }

        var scriptVisibleCookies = await Page.EvaluateAsync<string>("document.cookie");
        scriptVisibleCookies.Should().NotContain(
            BrowserTokenBoundaryWebApplicationFactory.AuthenticationCookieName);
        AssertTokenFree(scriptVisibleCookies);

        var authenticationCookies = (await Context.CookiesAsync())
            .Where(cookie => string.Equals(
                cookie.Name,
                BrowserTokenBoundaryWebApplicationFactory.AuthenticationCookieName,
                StringComparison.Ordinal))
            .ToArray();
        authenticationCookies.Should().ContainSingle();
        var authenticationCookie = authenticationCookies[0];
        authenticationCookie.HttpOnly.Should().BeTrue();
        authenticationCookie.Secure.Should().BeTrue();
        authenticationCookie.SameSite.Should().Be(SameSiteAttribute.Lax);
        authenticationCookie.Path.Should().Be("/");
        authenticationCookie.Value.Should().NotBeNullOrWhiteSpace();
        AssertTokenFree(authenticationCookie.Value);

        async Task<PageContent> VisitAsync(string relativePath)
        {
            var response = await Page.GotoAsync(
                new Uri(baseAddress, relativePath).AbsoluteUri,
                new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            var body = await RequireResponseBodyAsync(response);
            var dom = await Page.ContentAsync();
            await AssertBrowserStorageIsEmptyAsync();
            return new PageContent(body, dom);
        }
    }

    [BrowserFact]
    public async Task Operator_replay_form_fits_supported_desktop_and_mobile_viewports()
    {
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory();
        factory.StartServer();
        var baseAddress = factory.ClientOptions.BaseAddress;
        await Page.GotoAsync(
            new Uri(baseAddress, BrowserTokenBoundaryWebApplicationFactory.SignInPath).AbsoluteUri,
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        foreach (var viewport in new[]
                 {
                     new ViewportSize { Width = 1280, Height = 800 },
                     new ViewportSize { Width = 390, Height = 844 }
                 })
        {
            await Page.SetViewportSizeAsync(viewport.Width, viewport.Height);
            var response = await Page.GotoAsync(
                new Uri(
                    baseAddress,
                    $"/operator/dead-letters/{ReaderWebApplicationFactory.DeadLetterEventId:D}")
                    .AbsoluteUri,
                new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

            response.Should().NotBeNull();
            response!.Ok.Should().BeTrue();
            (await Page.Locator("form.replay-form").IsVisibleAsync()).Should().BeTrue();
            (await Page.Locator("textarea[name='Reason']").IsVisibleAsync()).Should().BeTrue();
            (await Page.Locator("form.replay-form button[type='submit']").IsVisibleAsync()).Should().BeTrue();
            var hasHorizontalOverflow = await Page.EvaluateAsync<bool>(
                "document.documentElement.scrollWidth > document.documentElement.clientWidth");
            hasHorizontalOverflow.Should().BeFalse();

            await CaptureScreenshotIfRequestedAsync("operator-replay", viewport);
        }
    }

    [BrowserFact]
    public async Task Operator_say_form_fits_supported_desktop_and_mobile_viewports()
    {
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory();
        factory.StartServer();
        var baseAddress = factory.ClientOptions.BaseAddress;
        await Page.GotoAsync(
            new Uri(baseAddress, BrowserTokenBoundaryWebApplicationFactory.SignInPath).AbsoluteUri,
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        foreach (var viewport in new[]
                 {
                     new ViewportSize { Width = 1280, Height = 800 },
                     new ViewportSize { Width = 390, Height = 844 }
                 })
        {
            await Page.SetViewportSizeAsync(viewport.Width, viewport.Height);
            var response = await Page.GotoAsync(
                new Uri(
                    baseAddress,
                    $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/commands/new")
                    .AbsoluteUri,
                new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

            response.Should().NotBeNull();
            response!.Ok.Should().BeTrue();
            (await Page.Locator("form.say-form").IsVisibleAsync()).Should().BeTrue();
            (await Page.Locator("textarea[name='Message']").IsVisibleAsync()).Should().BeTrue();
            (await Page.Locator("form.say-form button[type='submit']").IsVisibleAsync()).Should().BeTrue();
            var hasHorizontalOverflow = await Page.EvaluateAsync<bool>(
                "document.documentElement.scrollWidth > document.documentElement.clientWidth");
            hasHorizontalOverflow.Should().BeFalse();

            await CaptureScreenshotIfRequestedAsync("operator-say", viewport);
        }
    }

    [BrowserFact]
    public async Task Operator_monitoring_form_fits_supported_desktop_and_mobile_viewports()
    {
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory();
        factory.StartServer();
        var baseAddress = factory.ClientOptions.BaseAddress;
        await Page.GotoAsync(
            new Uri(baseAddress, BrowserTokenBoundaryWebApplicationFactory.SignInPath).AbsoluteUri,
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        foreach (var viewport in new[]
                 {
                     new ViewportSize { Width = 1280, Height = 800 },
                     new ViewportSize { Width = 390, Height = 844 }
                 })
        {
            await Page.SetViewportSizeAsync(viewport.Width, viewport.Height);
            var response = await Page.GotoAsync(
                new Uri(
                    baseAddress,
                    $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/monitoring")
                    .AbsoluteUri,
                new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

            response.Should().NotBeNull();
            response!.Ok.Should().BeTrue();
            (await Page.Locator("form.monitoring-form").IsVisibleAsync()).Should().BeTrue();
            (await Page.Locator("input[name='Confirmed']").IsVisibleAsync()).Should().BeTrue();
            (await Page.Locator("form.monitoring-form button[type='submit']").IsVisibleAsync())
                .Should().BeTrue();
            var hasHorizontalOverflow = await Page.EvaluateAsync<bool>(
                "document.documentElement.scrollWidth > document.documentElement.clientWidth");
            hasHorizontalOverflow.Should().BeFalse();

            await CaptureScreenshotIfRequestedAsync("operator-monitoring", viewport);
        }
    }

    [BrowserFact]
    public async Task Operator_registration_form_and_review_fit_supported_viewports()
    {
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory();
        factory.StartServer();
        var baseAddress = factory.ClientOptions.BaseAddress;
        await Page.GotoAsync(
            new Uri(baseAddress, BrowserTokenBoundaryWebApplicationFactory.SignInPath).AbsoluteUri,
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        foreach (var viewport in new[]
                 {
                     new ViewportSize { Width = 1280, Height = 800 },
                     new ViewportSize { Width = 390, Height = 844 }
                 })
        {
            await Page.SetViewportSizeAsync(viewport.Width, viewport.Height);
            var response = await Page.GotoAsync(
                new Uri(baseAddress, "/operator/servers/new").AbsoluteUri,
                new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

            response.Should().NotBeNull();
            response!.Ok.Should().BeTrue();
            (await Page.Locator("form.registration-form").IsVisibleAsync()).Should().BeTrue();
            (await Page.Locator("#server-name").IsVisibleAsync()).Should().BeTrue();
            (await Page.Locator("#server-host").IsVisibleAsync()).Should().BeTrue();
            (await Page.Locator("input[type='password']").CountAsync()).Should().Be(0);
            await Page.Locator("#server-name").FillAsync("Viewport test server");
            await Page.Locator("#server-host").FillAsync("game.example.test");
            await Page.Locator("form.registration-form button[type='submit']").ClickAsync();
            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            (await Page.Locator("form.registration-confirmation").IsVisibleAsync()).Should().BeTrue();
            (await Page.Locator("input[name='Confirmed']").IsVisibleAsync()).Should().BeTrue();
            (await Page.Locator("form.registration-confirmation button[type='submit']").IsVisibleAsync())
                .Should().BeTrue();
            var hasHorizontalOverflow = await Page.EvaluateAsync<bool>(
                "document.documentElement.scrollWidth > document.documentElement.clientWidth");
            hasHorizontalOverflow.Should().BeFalse();

            await CaptureScreenshotIfRequestedAsync("operator-registration-review", viewport);
        }
    }

    [BrowserFact]
    public async Task Operator_server_settings_form_and_review_fit_supported_viewports()
    {
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory(serverEnabled: false);
        factory.StartServer();
        var baseAddress = factory.ClientOptions.BaseAddress;
        await Page.GotoAsync(
            new Uri(baseAddress, BrowserTokenBoundaryWebApplicationFactory.SignInPath).AbsoluteUri,
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        foreach (var viewport in new[]
                 {
                     new ViewportSize { Width = 1280, Height = 800 },
                     new ViewportSize { Width = 390, Height = 844 }
                 })
        {
            await Page.SetViewportSizeAsync(viewport.Width, viewport.Height);
            var response = await Page.GotoAsync(
                new Uri(
                    baseAddress,
                    $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/settings")
                    .AbsoluteUri,
                new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

            response.Should().NotBeNull();
            response!.Ok.Should().BeTrue();
            (await Page.Locator("form.settings-form").IsVisibleAsync()).Should().BeTrue();
            (await Page.Locator("#server-name").IsVisibleAsync()).Should().BeTrue();
            (await Page.Locator("#server-host").IsVisibleAsync()).Should().BeTrue();
            (await Page.Locator("input[type='password']").CountAsync()).Should().Be(0);

            await Page.Locator("#server-name").FillAsync("Reviewed server");
            await Page.Locator("#server-host").FillAsync("reviewed.example.test");
            await Page.Locator("#query-port").FillAsync("27016");
            await Page.Locator("#rcon-port").FillAsync("27017");
            await Page.Locator("#poll-interval").FillAsync("45");
            await Page.Locator("#server-notes").FillAsync("Reviewed non-secret settings");
            await Page.Locator("form.settings-form button[type='submit']").ClickAsync();
            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            (await Page.Locator("section.review-section").IsVisibleAsync()).Should().BeTrue();
            (await Page.Locator("form.settings-confirmation").IsVisibleAsync()).Should().BeTrue();
            (await Page.Locator("input[name='Confirmed']").IsVisibleAsync()).Should().BeTrue();
            (await Page.Locator("form.settings-confirmation button[type='submit']").IsVisibleAsync())
                .Should().BeTrue();
            (await Page.Locator("input[type='password']").CountAsync()).Should().Be(0);
            (await Page.Locator("section.review-section").TextContentAsync())
                .Should().Contain("RCON credential").And.Contain("Unchanged");
            await CaptureScreenshotIfRequestedAsync("operator-server-settings-review", viewport);
            var overflowingElements = await Page.EvaluateAsync<string[]>(
                "Array.from(document.querySelectorAll('body *'))" +
                ".filter(element => { const bounds = element.getBoundingClientRect(); " +
                "return bounds.left < -0.5 || bounds.right > document.documentElement.clientWidth + 0.5; })" +
                ".map(element => `${element.tagName.toLowerCase()}.${element.className || ''} " +
                "[${element.getBoundingClientRect().left},${element.getBoundingClientRect().right}]`)");
            overflowingElements.Should().BeEmpty();
        }
    }

    [BrowserFact]
    public async Task Operator_rcon_credential_form_and_review_fit_supported_viewports()
    {
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory(serverEnabled: false);
        factory.StartServer();
        var baseAddress = factory.ClientOptions.BaseAddress;
        await Page.GotoAsync(
            new Uri(baseAddress, BrowserTokenBoundaryWebApplicationFactory.SignInPath).AbsoluteUri,
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        foreach (var viewport in new[]
                 {
                     new ViewportSize { Width = 1280, Height = 800 },
                     new ViewportSize { Width = 390, Height = 844 }
                 })
        {
            await Page.SetViewportSizeAsync(viewport.Width, viewport.Height);
            var response = await Page.GotoAsync(
                new Uri(
                    baseAddress,
                    $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/credentials")
                    .AbsoluteUri,
                new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

            response.Should().NotBeNull();
            response!.Ok.Should().BeTrue();
            (await Page.Locator("form.credential-form").IsVisibleAsync()).Should().BeTrue();
            (await Page.Locator("#credential-alias").IsVisibleAsync()).Should().BeTrue();
            (await Page.Locator("input[type='password']").CountAsync()).Should().Be(0);
            await Page.Locator("#credential-alias").FillAsync("viewport_server");
            await Page.Locator("form.credential-form button[type='submit']").ClickAsync();
            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            (await Page.Locator("section.review-section").IsVisibleAsync()).Should().BeTrue();
            (await Page.Locator("form.credential-confirmation").IsVisibleAsync()).Should().BeTrue();
            (await Page.Locator("input[name='Confirmed']").IsVisibleAsync()).Should().BeTrue();
            (await Page.Locator("input[type='password']").CountAsync()).Should().Be(0);
            (await Page.Locator("section.review-section").TextContentAsync())
                .Should().Contain("viewport_server").And.Contain("Raw secret");
            var hasHorizontalOverflow = await Page.EvaluateAsync<bool>(
                "document.documentElement.scrollWidth > document.documentElement.clientWidth");
            hasHorizontalOverflow.Should().BeFalse();

            await CaptureScreenshotIfRequestedAsync("operator-rcon-credential-review", viewport);
        }
    }

    [BrowserFact]
    public async Task Public_A2s_history_fits_supported_windows_and_viewports()
    {
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory();
        factory.StartServer();
        var baseAddress = factory.ClientOptions.BaseAddress;

        foreach (var historyWindow in new[]
                 {
                     new HistoryWindow("24h", ExpectedBuckets: 24),
                     new HistoryWindow("7d", ExpectedBuckets: 28)
                 })
        {
            foreach (var viewport in new[]
                     {
                         new ViewportSize { Width = 1280, Height = 800 },
                         new ViewportSize { Width = 390, Height = 844 }
                     })
            {
                await Page.SetViewportSizeAsync(viewport.Width, viewport.Height);
                var response = await Page.GotoAsync(
                    new Uri(baseAddress, $"/?range={historyWindow.Value}").AbsoluteUri,
                    new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

                response.Should().NotBeNull();
                response!.Ok.Should().BeTrue();
                (await Page.Locator("section.history").IsVisibleAsync()).Should().BeTrue();
                (await Page.Locator(".history-chart").IsVisibleAsync()).Should().BeTrue();
                (await Page.Locator(".history-bar").CountAsync()).Should().Be(historyWindow.ExpectedBuckets);
                (await Page.Locator(".range-switch__link[aria-current='page']").TextContentAsync())
                    .Should().Contain(string.Equals(historyWindow.Value, "7d", StringComparison.Ordinal)
                        ? "7 days"
                        : "24 hours");
                var hasHorizontalOverflow = await Page.EvaluateAsync<bool>(
                    "document.documentElement.scrollWidth > document.documentElement.clientWidth");
                hasHorizontalOverflow.Should().BeFalse();

                await CaptureScreenshotIfRequestedAsync(
                    $"public-a2s-{historyWindow.Value}",
                    viewport);
            }
        }
    }

    private async Task AssertBrowserStorageIsEmptyAsync()
    {
        var localStorageLength = await Page.EvaluateAsync<int>("localStorage.length");
        var sessionStorageLength = await Page.EvaluateAsync<int>("sessionStorage.length");
        localStorageLength.Should().Be(0);
        sessionStorageLength.Should().Be(0);
    }

    private async Task CaptureScreenshotIfRequestedAsync(string name, ViewportSize viewport)
    {
        var screenshotDirectory = Environment.GetEnvironmentVariable(ScreenshotDirectoryVariable);
        if (string.IsNullOrWhiteSpace(screenshotDirectory))
        {
            return;
        }

        Directory.CreateDirectory(screenshotDirectory);
        await Page.ScreenshotAsync(new PageScreenshotOptions
        {
            FullPage = true,
            Path = Path.Combine(
                screenshotDirectory,
                $"{name}-{viewport.Width}x{viewport.Height}.png")
        });
    }

    private static async Task<string> RequireResponseBodyAsync(IResponse? response)
    {
        response.Should().NotBeNull();
        response.Ok.Should().BeTrue();
        return await response.TextAsync();
    }

    private static void AssertTokenFree(string value)
    {
        value.Should().NotContain(BrowserTokenBoundaryWebApplicationFactory.AccessTokenSentinel);
        value.Should().NotContain(BrowserTokenBoundaryWebApplicationFactory.IdTokenSentinel);
        JwtPattern.IsMatch(value).Should().BeFalse();
        TokenFieldPattern.IsMatch(value).Should().BeFalse();
    }

    private sealed record PageContent(string Body, string Dom);

    private sealed record HistoryWindow(string Value, int ExpectedBuckets);

    [GeneratedRegex(
        @"(?<![A-Za-z0-9_-])eyJ[A-Za-z0-9_-]*\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+(?![A-Za-z0-9_-])",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex JwtPattern { get; }

    [GeneratedRegex(
        @"[\""']?(?:access|id|refresh)[_-]?token[\""']?\s*[:=]",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex TokenFieldPattern { get; }
}
