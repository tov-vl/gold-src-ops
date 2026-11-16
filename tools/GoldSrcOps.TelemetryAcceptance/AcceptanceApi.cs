using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using GoldSrcOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace GoldSrcOps.TelemetryAcceptance;

internal sealed class AcceptanceApi : WebApplicationFactory<Program>
{
    public const string Audience = "telemetry-acceptance";
    private const string Issuer = "https://telemetry-acceptance.invalid";
    private readonly SymmetricSecurityKey _key = new(RandomNumberGenerator.GetBytes(64));
    private readonly string _connectionString;
    private readonly string _repository;

    public AcceptanceApi(string repository, string connectionString)
    {
        _repository = repository;
        _connectionString = connectionString;
        UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
    }

    public string Token(Guid serverId, bool reader)
    {
        var claims = new List<Claim> { new("sub", reader ? "acceptance-reader" : "acceptance-agent") };
        if (reader)
        {
            claims.Add(new Claim("roles", "Reader"));
        }
        else
        {
            claims.Add(new Claim("permissions", "ingest:game-events"));
            claims.Add(new Claim("https://goldsrcops.com/claims/server_id", serverId.ToString("D")));
        }

        var token = new JwtSecurityToken(Issuer, Audience, claims,
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(10),
            new SigningCredentials(_key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public Uri Address => new(Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());

    public async Task<T> DatabaseAsync<T>(Func<GoldSrcOpsDbContext, Task<T>> action)
    {
        var scope = Services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            return await action(scope.ServiceProvider.GetRequiredService<GoldSrcOpsDbContext>()).ConfigureAwait(false);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(Path.Combine(_repository, "src", "GoldSrcOps.Api"));
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:GoldSrcOps", _connectionString);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ConnectionStrings:GoldSrcOps"] = _connectionString,
                ["Authentication:Schemes:Bearer:ValidIssuer"] = Issuer,
                ["Authentication:Schemes:Bearer:ValidAudiences:0"] = Audience,
                ["Authentication:Schemes:Bearer:RoleClaimType"] = "roles",
                ["Polling:Enabled"] = "false",
                ["CommandDispatcher:Enabled"] = "false",
                ["SnapshotRetention:Enabled"] = "false",
                ["GameEventRetention:Enabled"] = "false",
                ["AlertDelivery:Enabled"] = "false",
            }));
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.MapInboundClaims = false;
                options.Authority = null;
                options.MetadataAddress = string.Empty;
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
                    new OpenIdConnectConfiguration { Issuer = Issuer });
                options.TokenValidationParameters.IssuerSigningKey = _key;
                options.TokenValidationParameters.ValidateIssuerSigningKey = true;
            });
        });
    }
}
