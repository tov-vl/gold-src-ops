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

        var listResponse = await Page.GotoAsync(
            new Uri(baseAddress, BrowserTokenBoundaryWebApplicationFactory.SignInPath).AbsoluteUri,
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        var listBody = await RequireResponseBodyAsync(listResponse);
        var listDom = await Page.ContentAsync();
        await AssertBrowserStorageIsEmptyAsync();
        var detailResponse = await Page.GotoAsync(
            new Uri(
                baseAddress,
                $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}").AbsoluteUri,
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        var detailBody = await RequireResponseBodyAsync(detailResponse);
        var detailDom = await Page.ContentAsync();
        await AssertBrowserStorageIsEmptyAsync();

        Page.Url.Should().EndWith($"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}");
        listBody.Should().Contain(ReaderWebApplicationFactory.ServerName);
        detailBody.Should().Contain("Latest observation");
        AssertTokenFree(listBody);
        AssertTokenFree(listDom);
        AssertTokenFree(detailBody);
        AssertTokenFree(detailDom);

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
