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
    private readonly string _databaseName = $"goldsrcops-tests-{Guid.NewGuid():N}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ConnectionStrings:GoldSrcOps"] = "Host=localhost;Database=goldsrcops_tests;Username=test;Password=test",
                ["Polling:Enabled"] = "false"
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
                options.UseInMemoryDatabase(_databaseName));
        });
    }
}
