using GoldSrcOps.Web.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace GoldSrcOps.Web.Endpoints;

internal static class AuthenticationEndpoints
{
    private const string DefaultLoginReturnUrl = "/operator/servers";

    public static IEndpointRouteBuilder MapAuthenticationEndpoints(
        this IEndpointRouteBuilder endpoints,
        bool authenticationEnabled)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/auth");
        group.MapGet("/login", (string? returnUrl) =>
        {
            if (!authenticationEnabled)
            {
                return Results.NotFound();
            }

            var properties = new AuthenticationProperties
            {
                RedirectUri = GetLocalReturnUrl(returnUrl, DefaultLoginReturnUrl)
            };
            return Results.Challenge(
                properties,
                [OpenIdConnectDefaults.AuthenticationScheme]);
        })
            .AllowAnonymous();

        group.MapPost("/logout", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            await antiforgery.ValidateRequestAsync(context);

            var properties = new AuthenticationProperties
            {
                RedirectUri = "/"
            };
            return Results.SignOut(
                properties,
                [
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    OpenIdConnectDefaults.AuthenticationScheme
                ]);
        })
            .RequireAuthorization();

        return endpoints;
    }

    internal static string GetLocalReturnUrl(string? returnUrl, string fallback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fallback);

        if (string.IsNullOrWhiteSpace(returnUrl) ||
            returnUrl[0] != '/' ||
            (returnUrl.Length > 1 && (returnUrl[1] == '/' || returnUrl[1] == '\\')) ||
            returnUrl.Contains('\r') ||
            returnUrl.Contains('\n'))
        {
            return fallback;
        }

        return returnUrl;
    }
}
