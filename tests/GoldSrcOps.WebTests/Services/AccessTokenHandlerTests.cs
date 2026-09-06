using System.Net;
using System.Security.Claims;
using AwesomeAssertions;
using GoldSrcOps.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace GoldSrcOps.WebTests.Services;

public sealed class AccessTokenHandlerTests
{
    [Fact]
    public async Task SendAsync_forwards_access_token_as_bearer_header()
    {
        var properties = new AuthenticationProperties();
        properties.StoreTokens(
        [
            new AuthenticationToken { Name = "access_token", Value = "reader-access-token" }
        ]);
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity("Test")),
            properties,
            CookieAuthenticationDefaults.AuthenticationScheme);
        var context = CreateHttpContext(AuthenticateResult.Success(ticket));
        var capture = new CaptureHandler();
        using var handler = new AccessTokenHandler(new HttpContextAccessor { HttpContext = context })
        {
            InnerHandler = capture
        };
        using var client = new HttpClient(handler);

        using var response = await client.GetAsync("https://api.example.test/api/servers/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        capture.AuthorizationScheme.Should().Be("Bearer");
        capture.AuthorizationParameter.Should().Be("reader-access-token");
    }

    [Fact]
    public async Task SendAsync_rejects_session_without_access_token()
    {
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity("Test")),
            new AuthenticationProperties(),
            CookieAuthenticationDefaults.AuthenticationScheme);
        var context = CreateHttpContext(AuthenticateResult.Success(ticket));
        using var handler = new AccessTokenHandler(new HttpContextAccessor { HttpContext = context })
        {
            InnerHandler = new CaptureHandler()
        };
        using var client = new HttpClient(handler);

        var action = () => client.GetAsync("https://api.example.test/api/servers/");

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not contain an API access token*");
    }

    private static DefaultHttpContext CreateHttpContext(AuthenticateResult result)
    {
        var services = new ServiceCollection()
            .AddSingleton<IAuthenticationService>(new StubAuthenticationService(result))
            .BuildServiceProvider();
        return new DefaultHttpContext { RequestServices = services };
    }

    private sealed class StubAuthenticationService(AuthenticateResult result) : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(result);

        public Task ChallengeAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? properties) => Task.CompletedTask;

        public Task ForbidAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? properties) => Task.CompletedTask;

        public Task SignInAsync(
            HttpContext context,
            string? scheme,
            ClaimsPrincipal principal,
            AuthenticationProperties? properties) => Task.CompletedTask;

        public Task SignOutAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? properties) => Task.CompletedTask;
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? AuthorizationScheme { get; private set; }

        public string? AuthorizationParameter { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
