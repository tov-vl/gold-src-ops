using GoldSrcOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;

namespace GoldSrcOps.UnitTests.Api;

internal sealed class PostgreSqlGoldSrcOpsApiFactory : WebApplicationFactory<Program>
{
    private readonly Action<IServiceCollection>? _configureTestServices;
    private readonly PostgreSqlContainer _database;
    private readonly TestApiPrincipal _principal;

    private PostgreSqlGoldSrcOpsApiFactory(
        Action<IServiceCollection>? configureTestServices,
        TestApiPrincipal? principal)
    {
        _configureTestServices = configureTestServices;
        _principal = principal ?? TestApiPrincipal.Operator();
        _database = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("goldsrcops_tests")
            .WithUsername("goldsrcops")
            .WithPassword("goldsrcops")
            .Build();
    }

    public static async Task<PostgreSqlGoldSrcOpsApiFactory> CreateAsync(
        Action<IServiceCollection>? configureTestServices = null,
        TestApiPrincipal? principal = null)
    {
        var factory = new PostgreSqlGoldSrcOpsApiFactory(configureTestServices, principal);

        try
        {
            await factory.InitializeDatabaseAsync();
            return factory;
        }
        catch
        {
            await factory.DisposeAsync();
            throw;
        }
    }

    public async Task ExecuteDbContextAsync(Func<GoldSrcOpsDbContext, Task> action)
    {
        using var scope = Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GoldSrcOpsDbContext>();

        await action(dbContext);
    }

    public async Task<T> ExecuteDbContextAsync<T>(Func<GoldSrcOpsDbContext, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GoldSrcOpsDbContext>();

        return await action(dbContext);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _database.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Authentication:Schemes:Bearer:ValidAudiences:0"] = "goldsrcops-tests",
                ["Authentication:Schemes:Bearer:ValidIssuer"] = "goldsrcops-tests",
                ["ConnectionStrings:GoldSrcOps"] = _database.GetConnectionString(),
                ["CommandDispatcher:Enabled"] = "false",
                ["Polling:Enabled"] = "false",
                ["SnapshotRetention:Enabled"] = "false"
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<GoldSrcOpsDbContext>();
            services.RemoveAll<DbContextOptions>();
            services.RemoveAll<DbContextOptions<GoldSrcOpsDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<GoldSrcOpsDbContext>>();

            services.AddDbContext<GoldSrcOpsDbContext>(options =>
                options.UseNpgsql(
                    _database.GetConnectionString(),
                    npgsql => npgsql.MigrationsAssembly(typeof(GoldSrcOpsDbContext).Assembly.FullName)));

            _configureTestServices?.Invoke(services);
            services.AddGoldSrcOpsTestAuthentication(_principal);
        });
    }

    private async Task InitializeDatabaseAsync()
    {
        await _database.StartAsync();

        await using var scope = Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GoldSrcOpsDbContext>();
        await dbContext.Database.MigrateAsync();
    }
}
