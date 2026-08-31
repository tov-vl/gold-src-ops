using System.Security.Claims;
using AwesomeAssertions;
using GoldSrcOps.Api.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Moq;

namespace GoldSrcOps.UnitTests.Api;

public sealed class SecurityServiceCollectionExtensionsTests
{
    private const string CustomRoleClaimType = "https://goldsrcops.com/roles";

    [Fact]
    public void AddGoldSrcOpsSecurity_uses_framework_role_claim_type_by_default()
    {
        using var serviceProvider = CreateServiceProvider(roleClaimType: null);

        var options = GetBearerOptions(serviceProvider);

        options.TokenValidationParameters.RoleClaimType.Should().Be(ClaimTypes.Role);
    }

    [Fact]
    public void AddGoldSrcOpsSecurity_applies_configured_role_claim_type()
    {
        using var serviceProvider = CreateServiceProvider(CustomRoleClaimType);

        var options = GetBearerOptions(serviceProvider);

        options.TokenValidationParameters.RoleClaimType.Should().Be(CustomRoleClaimType);
    }

    [Fact]
    public async Task Configured_role_claim_type_drives_principal_role_membership()
    {
        var signingKey = new SymmetricSecurityKey(new byte[32]);
        await using var serviceProvider = CreateServiceProvider(CustomRoleClaimType, signingKey.Key);
        var options = GetBearerOptions(serviceProvider);
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Audience = "goldsrcops-tests",
            Claims = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [CustomRoleClaimType] = new[] { GoldSrcOpsSecurity.ReaderRole },
            },
            Expires = DateTime.UtcNow.AddMinutes(5),
            Issuer = "goldsrcops-tests",
            SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256),
            Subject = new ClaimsIdentity(
            [
                new Claim(GoldSrcOpsSecurity.SubjectClaimType, "reader-42"),
            ]),
        });

        var validationResult = await new JsonWebTokenHandler()
            .ValidateTokenAsync(token, options.TokenValidationParameters);

        validationResult.IsValid.Should().BeTrue();
        new ClaimsPrincipal(validationResult.ClaimsIdentity)
            .IsInRole(GoldSrcOpsSecurity.ReaderRole)
            .Should()
            .BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" roles")]
    [InlineData("roles ")]
    public void AddGoldSrcOpsSecurity_rejects_invalid_role_claim_type(string roleClaimType)
    {
        using var serviceProvider = CreateServiceProvider(roleClaimType);

        var action = () => GetBearerOptions(serviceProvider);

        action.Should()
            .Throw<OptionsValidationException>()
            .WithMessage("*valid role claim type*");
    }

    private static JwtBearerOptions GetBearerOptions(IServiceProvider serviceProvider) =>
        serviceProvider
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

    private static ServiceProvider CreateServiceProvider(
        string? roleClaimType,
        byte[]? signingKey = null)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Authentication:Schemes:Bearer:ValidAudiences:0"] = "goldsrcops-tests",
            ["Authentication:Schemes:Bearer:ValidIssuer"] = "goldsrcops-tests",
        };
        if (roleClaimType is not null)
        {
            settings["Authentication:Schemes:Bearer:RoleClaimType"] = roleClaimType;
        }
        if (signingKey is not null)
        {
            settings["Authentication:Schemes:Bearer:SigningKeys:0:Issuer"] = "goldsrcops-tests";
            settings["Authentication:Schemes:Bearer:SigningKeys:0:Value"] = Convert.ToBase64String(signingKey);
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(static value => value.EnvironmentName).Returns(Environments.Development);

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddGoldSrcOpsSecurity(environment.Object);

        return services.BuildServiceProvider();
    }
}
