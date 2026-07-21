using System.Security.Claims;
using System.Text.Encodings.Web;
using GoldSrcOps.Api.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GoldSrcOps.UnitTests.Api;

internal sealed record TestApiPrincipal(
    bool IsAuthenticated,
    string? Subject,
    IReadOnlyCollection<string> Roles)
{
    public static TestApiPrincipal Anonymous { get; } = new(false, null, []);

    public static TestApiPrincipal Reader(string subject = "reader") =>
        new(true, subject, [GoldSrcOpsSecurity.ReaderRole]);

    public static TestApiPrincipal Operator(string subject = "admin") =>
        new(true, subject, [GoldSrcOpsSecurity.OperatorRole]);

    public static TestApiPrincipal WithoutRoles(string subject = "authenticated") =>
        new(true, subject, []);

    public static TestApiPrincipal WithoutSubject() =>
        new(true, null, [GoldSrcOpsSecurity.OperatorRole]);
}

internal static class TestAuthentication
{
    public const string Scheme = "GoldSrcOpsTest";

    public static IServiceCollection AddGoldSrcOpsTestAuthentication(
        this IServiceCollection services,
        TestApiPrincipal principal)
    {
        services.AddSingleton(principal);
        services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = Scheme;
                options.DefaultChallengeScheme = Scheme;
                options.DefaultForbidScheme = Scheme;
                options.DefaultScheme = Scheme;
            })
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(Scheme, static _ => { });

        return services;
    }
}

internal sealed class TestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    TestApiPrincipal testPrincipal)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!testPrincipal.IsAuthenticated)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim>();
        if (testPrincipal.Subject is not null)
        {
            claims.Add(new Claim(GoldSrcOpsSecurity.SubjectClaimType, testPrincipal.Subject));
        }

        claims.AddRange(testPrincipal.Roles.Select(static role => new Claim(ClaimTypes.Role, role)));

        var identity = new ClaimsIdentity(
            claims,
            TestAuthentication.Scheme,
            GoldSrcOpsSecurity.SubjectClaimType,
            ClaimTypes.Role);
        var principal = new ClaimsPrincipal(identity);
        if (!GoldSrcOpsSecurity.TryGetSubject(principal, out _))
        {
            return Task.FromResult(
                AuthenticateResult.Fail("The test principal subject is missing or invalid."));
        }

        var ticket = new AuthenticationTicket(principal, TestAuthentication.Scheme);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
