using System.Text.RegularExpressions;
using AwesomeAssertions;
using GoldSrcOps.WebTests.Pages;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit;

namespace GoldSrcOps.WebTests.Browser;

public sealed partial class BrowserTokenBoundaryTests : PageTest
{
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
        var incidentsPage = await VisitAsync("/operator/incidents");
        var deadLettersPage = await VisitAsync("/operator/dead-letters");
        var deadLetterDetailPage = await VisitAsync(
            $"/operator/dead-letters/{ReaderWebApplicationFactory.DeadLetterEventId:D}");

        Page.Url.Should().EndWith($"/operator/dead-letters/{ReaderWebApplicationFactory.DeadLetterEventId:D}");
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
        foreach (var page in new[]
                 {
                     listPage,
                     detailPage,
                     historyPage,
                     commandsPage,
                     incidentsPage,
                     deadLettersPage,
                     deadLetterDetailPage
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

    private async Task AssertBrowserStorageIsEmptyAsync()
    {
        var localStorageLength = await Page.EvaluateAsync<int>("localStorage.length");
        var sessionStorageLength = await Page.EvaluateAsync<int>("sessionStorage.length");
        localStorageLength.Should().Be(0);
        sessionStorageLength.Should().Be(0);
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
