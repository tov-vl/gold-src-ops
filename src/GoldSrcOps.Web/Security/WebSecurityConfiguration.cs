using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace GoldSrcOps.Web.Security;

internal static class WebSecurityConfiguration
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(55);

    public static bool Configure(
        IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var settings = configuration
            .GetSection(WebAuthenticationOptions.SectionName)
            .Get<WebAuthenticationOptions>() ?? new WebAuthenticationOptions();

        services.AddSingleton(new WebAuthenticationState(settings.Enabled));
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<InMemoryTicketStore>();
        services.AddSingleton<OperatorCommandConfirmationStore>();
        services.AddHttpContextAccessor();

        var authentication = services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = settings.Enabled
                    ? OpenIdConnectDefaults.AuthenticationScheme
                    : CookieAuthenticationDefaults.AuthenticationScheme;
            })
            .AddCookie(options =>
            {
                options.Cookie.Name = environment.IsDevelopment()
                    ? ".GoldSrcOps.Web"
                    : "__Host-GoldSrcOps.Web";
                options.Cookie.HttpOnly = true;
                options.Cookie.IsEssential = true;
                options.Cookie.Path = "/";
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = environment.IsDevelopment()
                    ? CookieSecurePolicy.SameAsRequest
                    : CookieSecurePolicy.Always;
                options.LoginPath = "/auth/login";
                options.AccessDeniedPath = "/auth/forbidden";
                options.ExpireTimeSpan = SessionLifetime;
                options.SlidingExpiration = false;
                if (!settings.Enabled)
                {
                    options.Events.OnRedirectToLogin = static context =>
                    {
                        context.Response.StatusCode = StatusCodes.Status404NotFound;
                        return Task.CompletedTask;
                    };
                    options.Events.OnRedirectToAccessDenied = static context =>
                    {
                        context.Response.StatusCode = StatusCodes.Status404NotFound;
                        return Task.CompletedTask;
                    };
                }
            });

        services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
            .Configure<InMemoryTicketStore>(static (options, ticketStore) =>
            {
                options.SessionStore = ticketStore;
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(
                WebSecurity.ReaderPolicy,
                static policy => policy
                    .RequireAuthenticatedUser()
                    .RequireRole(WebSecurity.ReaderRole, WebSecurity.OperatorRole))
            .AddPolicy(
                WebSecurity.OperatorPolicy,
                static policy => policy
                    .RequireAuthenticatedUser()
                    .RequireRole(WebSecurity.OperatorRole)
                    .RequireClaim(WebSecurity.SubjectClaim)
                    .RequireAssertion(static context =>
                        WebSecurity.GetSubject(context.User) is not null));

        if (!settings.Enabled)
        {
            return false;
        }

        var authority = GetRequiredHttpsUri(settings.Authority, "Authentication:Authority");
        var audience = GetRequiredTrimmed(settings.Audience, "Authentication:Audience");
        var clientId = GetRequiredTrimmed(settings.ClientId, "Authentication:ClientId");
        var roleClaimType = GetRequiredTrimmed(
            settings.RoleClaimType,
            "Authentication:RoleClaimType");
        var clientSecret = SecretFileReader.ReadRequiredSecret(
            settings.ClientSecret,
            settings.ClientSecretFile,
            environment.IsDevelopment(),
            "Authentication:ClientSecret");

        authentication.AddOpenIdConnect(options =>
        {
            options.Authority = authority.AbsoluteUri;
            options.ClientId = clientId;
            options.ClientSecret = clientSecret;
            options.ResponseType = OpenIdConnectResponseType.Code;
            options.UsePkce = true;
            options.SaveTokens = true;
            options.MapInboundClaims = false;
            options.GetClaimsFromUserInfoEndpoint = false;
            options.CallbackPath = "/signin-oidc";
            options.SignedOutCallbackPath = "/signout-callback-oidc";
            options.RemoteAuthenticationTimeout = TimeSpan.FromMinutes(2);
            options.TokenValidationParameters = new TokenValidationParameters
            {
                NameClaimType = "name",
                RoleClaimType = roleClaimType
            };
            options.Scope.Clear();
            options.Scope.Add(OpenIdConnectScope.OpenIdProfile);
            options.Scope.Add(OpenIdConnectScope.Email);
            options.Events.OnRedirectToIdentityProvider = context =>
            {
                context.ProtocolMessage.SetParameter("audience", audience);
                return Task.CompletedTask;
            };
            options.Events.OnRemoteFailure = context =>
            {
                context.HandleResponse();
                context.Response.Redirect("/auth/error");
                return Task.CompletedTask;
            };
        });

        return true;
    }

    private static Uri GetRequiredHttpsUri(string? value, string settingName)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var result) ||
            !string.Equals(result.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Configuration value '{settingName}' must be an absolute HTTPS URI.");
        }

        return result;
    }

    private static string GetRequiredTrimmed(string? value, string settingName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Configuration value '{settingName}' must be non-empty and have no surrounding whitespace.");
        }

        return value;
    }
}
