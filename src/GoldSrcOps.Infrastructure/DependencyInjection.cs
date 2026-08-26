using System.Text;
using GoldSrcOps.Application.Alerts;
using GoldSrcOps.Application.Commands;
using GoldSrcOps.Application.Common;
using GoldSrcOps.Application.Credentials;
using GoldSrcOps.Application.Incidents;
using GoldSrcOps.Application.Monitoring;
using GoldSrcOps.Application.Servers;
using GoldSrcOps.Infrastructure.A2S;
using GoldSrcOps.Infrastructure.Alerts;
using GoldSrcOps.Infrastructure.Commands;
using GoldSrcOps.Infrastructure.Monitoring;
using GoldSrcOps.Infrastructure.Persistence;
using GoldSrcOps.Infrastructure.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GoldSrcOps.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var connectionString = configuration.GetConnectionString("GoldSrcOps")
            ?? throw new InvalidOperationException("Connection string 'GoldSrcOps' is not configured.");

        services.AddDbContext<GoldSrcOpsDbContext>(options =>
            options.UseNpgsql(connectionString, GoldSrcOpsNpgsqlOptions.Configure));

        var pollingOptions = GoldSrcPollingOptions.FromConfiguration(configuration);
        var rconOptions = GoldSrcRconOptions.FromConfiguration(configuration);
        var dispatcherOptions = CommandDispatcherOptions.FromConfiguration(configuration);
        var snapshotRetentionOptions = SnapshotRetentionOptions.FromConfiguration(configuration);
        var alertDeliveryOptions = AlertDeliveryOptions.FromConfiguration(
            configuration,
            allowHttpEndpoint: environment.IsDevelopment());

        if (dispatcherOptions.Enabled && dispatcherOptions.InterruptedAfter <= rconOptions.Timeout)
        {
            throw new InvalidOperationException(
                "CommandDispatcher:InterruptedAfterSeconds must exceed the configured RCON timeout.");
        }

        services.AddSingleton(pollingOptions);
        services.AddSingleton(rconOptions);
        services.AddSingleton(dispatcherOptions);
        services.AddSingleton(snapshotRetentionOptions);
        services.AddSingleton(alertDeliveryOptions);
        services.AddSingleton(new ServerPollingSettings(
            pollingOptions.QueryTimeout,
            pollingOptions.BatchSize,
            pollingOptions.IncidentFailureThreshold));
        services.AddSingleton(new SnapshotRetentionSettings(
            snapshotRetentionOptions.RetentionPeriod,
            snapshotRetentionOptions.BatchSize));
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IAlertDeliveryReadRepository, EfAlertDeliveryReadRepository>();
        services.AddScoped<IAlertDeliveryReplayRepository, EfAlertDeliveryReplayRepository>();
        services.AddScoped<IOutboxStore, EfOutboxStore>();
        services.AddScoped<IOutboxWriter, EfOutboxWriter>();
        services.AddScoped<IServerRepository, EfServerRepository>();
        services.AddScoped<IIncidentRepository, EfIncidentRepository>();
        services.AddScoped<IMonitoringReadRepository, EfMonitoringReadRepository>();
        services.AddScoped<IPollSnapshotRetentionRepository, EfPollSnapshotRetentionRepository>();
        services.AddScoped<IServerCredentialRepository, EfServerCredentialRepository>();
        services.AddScoped<ICommandExecutionRepository, EfCommandExecutionRepository>();
        services.AddSingleton<ISecretReferenceResolver, ConfigurationSecretReferenceResolver>();
        services.AddSingleton<IGoldSrcRconClient>(_ =>
            new GoldSrcRconClient(Encoding.GetEncoding("windows-1251")));
        services.AddScoped<IRconCommandExecutor, GoldSrcRconCommandExecutor>();
        services.AddScoped<CommandDispatcher>();
        services.AddScoped<ServerPollingService>();
        services.AddScoped<SnapshotRetentionService>();
        services.AddSingleton<IGoldSrcServerQueryClient>(_ =>
            new GoldSrcServerQueryClient(Encoding.GetEncoding("windows-1251")));

        if (pollingOptions.Enabled)
        {
            services.AddHostedService<GoldSrcPollingBackgroundService>();
        }

        if (dispatcherOptions.Enabled)
        {
            services.AddHostedService<CommandDispatchBackgroundService>();
        }

        if (snapshotRetentionOptions.Enabled)
        {
            services.AddHostedService<SnapshotRetentionBackgroundService>();
        }

        if (alertDeliveryOptions.Enabled)
        {
            var dispatcherSettings = new AlertDispatcherSettings(
                alertDeliveryOptions.ClaimTimeout,
                alertDeliveryOptions.MaxAttempts,
                alertDeliveryOptions.BaseRetryDelay,
                alertDeliveryOptions.MaximumRetryDelay,
                alertDeliveryOptions.ProcessedRetentionPeriod,
                alertDeliveryOptions.CleanupBatchSize);
            var webhookSettings = new HttpWebhookDeliverySettings(
                alertDeliveryOptions.WebhookEndpoint!,
                alertDeliveryOptions.RequestTimeout,
                alertDeliveryOptions.MaximumRetryDelay,
                alertDeliveryOptions.Authorization);

            services.AddSingleton(dispatcherSettings);
            services.AddSingleton<IAlertRetryDelayProvider, ExponentialJitterAlertRetryDelayProvider>();
            services.AddSingleton(webhookSettings);
            services.AddSingleton<IAlertDeliveryChannel, HttpWebhookAlertDeliveryChannel>();
            services.AddScoped<AlertDispatcher>();
            services.AddHostedService<AlertDispatchBackgroundService>();
        }

        return services;
    }
}
