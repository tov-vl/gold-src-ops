using System.Text;
using GoldSrcOps.Application.Common;
using GoldSrcOps.Application.Incidents;
using GoldSrcOps.Application.Monitoring;
using GoldSrcOps.Application.Servers;
using GoldSrcOps.Infrastructure.A2S;
using GoldSrcOps.Infrastructure.Monitoring;
using GoldSrcOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GoldSrcOps.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var connectionString = configuration.GetConnectionString("GoldSrcOps")
            ?? throw new InvalidOperationException("Connection string 'GoldSrcOps' is not configured.");

        services.AddDbContext<GoldSrcOpsDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(typeof(GoldSrcOpsDbContext).Assembly.FullName)));

        var pollingOptions = GoldSrcPollingOptions.FromConfiguration(configuration);

        services.AddSingleton(pollingOptions);
        services.AddSingleton(new ServerPollingSettings(
            pollingOptions.QueryTimeout,
            pollingOptions.BatchSize,
            pollingOptions.IncidentFailureThreshold));
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<IServerRepository, EfServerRepository>();
        services.AddScoped<IIncidentRepository, EfIncidentRepository>();
        services.AddScoped<IMonitoringReadRepository, EfMonitoringReadRepository>();
        services.AddScoped<ServerPollingService>();
        services.AddSingleton<IGoldSrcServerQueryClient>(_ =>
            new GoldSrcServerQueryClient(Encoding.GetEncoding("windows-1251")));

        if (pollingOptions.Enabled)
        {
            services.AddHostedService<GoldSrcPollingBackgroundService>();
        }

        return services;
    }
}
