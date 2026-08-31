using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

namespace GoldSrcOps.Api.Security;

internal static class SecurityServiceCollectionExtensions
{
    private const string RoleClaimTypeConfigurationKey =
        "Authentication:Schemes:Bearer:RoleClaimType";
    private static readonly TimeSpan AccessTokenClockSkew = TimeSpan.FromSeconds(30);

    public static IServiceCollection AddGoldSrcOpsSecurity(
        this IServiceCollection services,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(environment);

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .PostConfigure<IConfiguration>(
                static (options, configuration) =>
                {
                    var configuredRoleClaimType = configuration[RoleClaimTypeConfigurationKey];
                    if (HasValidRoleClaimTypeConfiguration(configuredRoleClaimType) &&
                        configuredRoleClaimType is not null)
                    {
                        options.TokenValidationParameters.RoleClaimType = configuredRoleClaimType;
                    }
                    else if (configuredRoleClaimType is null)
                    {
                        options.TokenValidationParameters.RoleClaimType = ClaimTypes.Role;
                    }

                    options.TokenValidationParameters.RequireExpirationTime = true;
                    options.TokenValidationParameters.RequireSignedTokens = true;
                    options.TokenValidationParameters.ValidateAudience = true;
                    options.TokenValidationParameters.ValidateIssuer = true;
                    options.TokenValidationParameters.ValidateLifetime = true;
                    options.TokenValidationParameters.ClockSkew = AccessTokenClockSkew;

                    var existingHandler = options.Events.OnTokenValidated;
                    options.Events.OnTokenValidated = async context =>
                    {
                        if (existingHandler is not null)
                        {
                            await existingHandler(context).ConfigureAwait(false);
                        }

                        if (!GoldSrcOpsSecurity.TryGetSubject(context.Principal, out _))
                        {
                            context.Fail("The access token subject is missing or invalid.");
                        }
                    };
                })
            .Validate(HasIssuerConfiguration, "Bearer authentication requires a valid issuer or authority.")
            .Validate(HasAudienceConfiguration, "Bearer authentication requires at least one valid audience.")
            .Validate<IConfiguration>(
                static (_, configuration) => HasValidRoleClaimTypeConfiguration(
                    configuration[RoleClaimTypeConfigurationKey]),
                "Bearer authentication requires a valid role claim type.")
            .Validate(
                options => environment.IsDevelopment() || options.RequireHttpsMetadata,
                "Bearer metadata must require HTTPS outside Development.")
            .ValidateOnStart();

        services.AddAuthorizationBuilder()
            .AddPolicy(
                GoldSrcOpsSecurity.ReaderPolicy,
                static policy => policy
                    .RequireAuthenticatedUser()
                    .RequireRole(GoldSrcOpsSecurity.ReaderRole, GoldSrcOpsSecurity.OperatorRole))
            .AddPolicy(
                GoldSrcOpsSecurity.OperatorPolicy,
                static policy => policy
                    .RequireAuthenticatedUser()
                    .RequireRole(GoldSrcOpsSecurity.OperatorRole))
            .SetFallbackPolicy(
                new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .RequireRole(GoldSrcOpsSecurity.OperatorRole)
                    .Build());

        return services;
    }

    private static bool HasIssuerConfiguration(JwtBearerOptions options)
    {
        return !string.IsNullOrWhiteSpace(options.Authority) ||
            !string.IsNullOrWhiteSpace(options.MetadataAddress) ||
            !string.IsNullOrWhiteSpace(options.TokenValidationParameters.ValidIssuer) ||
            options.TokenValidationParameters.ValidIssuers?.Any(static issuer =>
                !string.IsNullOrWhiteSpace(issuer)) == true;
    }

    private static bool HasAudienceConfiguration(JwtBearerOptions options)
    {
        return !string.IsNullOrWhiteSpace(options.Audience) ||
            !string.IsNullOrWhiteSpace(options.TokenValidationParameters.ValidAudience) ||
            options.TokenValidationParameters.ValidAudiences?.Any(static audience =>
                !string.IsNullOrWhiteSpace(audience)) == true;
    }

    private static bool HasValidRoleClaimTypeConfiguration(string? roleClaimType)
    {
        return roleClaimType is null ||
            (!string.IsNullOrWhiteSpace(roleClaimType) &&
                string.Equals(roleClaimType, roleClaimType.Trim(), StringComparison.Ordinal));
    }
}
