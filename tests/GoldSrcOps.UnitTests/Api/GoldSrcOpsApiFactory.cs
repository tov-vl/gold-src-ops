using GoldSrcOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace GoldSrcOps.UnitTests.Api;

internal sealed class GoldSrcOpsApiFactory : WebApplicationFactory<Program>
{
    private readonly bool _commandDispatcherEnabled;
    private readonly Action<IServiceCollection>? _configureTestServices;
    private readonly string _databaseName = $"goldsrcops-tests-{Guid.NewGuid():N}";
    private readonly TestApiPrincipal _principal;

    public GoldSrcOpsApiFactory(
        Action<IServiceCollection>? configureTestServices = null,
        TestApiPrincipal? principal = null,
        bool commandDispatcherEnabled = false)
    {
        _configureTestServices = configureTestServices;
        _principal = principal ?? TestApiPrincipal.Operator();
        _commandDispatcherEnabled = commandDispatcherEnabled;
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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting(
            "ConnectionStrings:GoldSrcOps",
            "Host=localhost;Database=goldsrcops_tests;Username=test;Password=test");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Authentication:Schemes:Bearer:ValidAudiences:0"] = "goldsrcops-tests",
                ["Authentication:Schemes:Bearer:ValidIssuer"] = "goldsrcops-tests",
                ["CommandDispatcher:Enabled"] = _commandDispatcherEnabled.ToString(),
                ["CommandDispatcher:LoopDelayMilliseconds"] = "10",
                ["CommandDispatcher:MaxConcurrency"] = "1",
                ["CommandDispatcher:RecoveryIntervalSeconds"] = "1",
                ["ConnectionStrings:GoldSrcOps"] = "Host=localhost;Database=goldsrcops_tests;Username=test;Password=test",
                ["Polling:Enabled"] = "false",
                ["SnapshotRetention:Enabled"] = "false"
            });
        });

        builder.ConfigureServices(services =>
        {
            if (!_commandDispatcherEnabled)
            {
                services.RemoveAll<IHostedService>();
            }

            services.RemoveAll<GoldSrcOpsDbContext>();
            services.RemoveAll<DbContextOptions>();
            services.RemoveAll<DbContextOptions<GoldSrcOpsDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<GoldSrcOpsDbContext>>();

            services.AddDbContext<GoldSrcOpsDbContext>(options =>
                options.UseInMemoryDatabase(_databaseName));

            _configureTestServices?.Invoke(services);
            services.AddGoldSrcOpsTestAuthentication(_principal);
        });
    }
}
