using System.Text.Json.Serialization;
using GoldSrcOps.AlertReceiver.AvailabilityEvents;
using GoldSrcOps.AlertReceiver.Configuration;
using GoldSrcOps.AlertReceiver.Persistence;
using GoldSrcOps.AlertReceiver.ProviderDelivery;
using GoldSrcOps.AlertReceiver.ProviderOperations;
using GoldSrcOps.AlertReceiver.Telemetry;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("AlertReceiver");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "Connection string 'AlertReceiver' must be configured.");
}

builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(static options =>
{
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
});
builder.Services
    .AddOptions<AlertReceiverOptions>()
    .Bind(builder.Configuration.GetSection(AlertReceiverOptions.SectionName))
    .Validate(
        static options => Enum.IsDefined(options.Mode),
        "Receiver mode must be CatchUp or Live.")
    .Validate(
        static options => !string.IsNullOrWhiteSpace(options.Authorization),
        "Receiver authorization must be configured.")
    .Validate(
        static options => options.Authorization is not null &&
            options.Authorization.Length <= AlertReceiverOptions.MaxAuthorizationLength,
        $"Receiver authorization must not exceed {AlertReceiverOptions.MaxAuthorizationLength} characters.")
    .ValidateOnStart();
builder.Services.AddSingleton<ReceiverAuthorization>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services
    .AddOptions<ProviderOperationsOptions>()
    .Bind(builder.Configuration.GetSection(ProviderOperationsOptions.SectionName))
    .Validate(
        static options => options.IsValid(),
        "Provider operations settings are invalid; enabled operations require bounded authorization.")
    .ValidateOnStart();
builder.Services.AddSingleton<ProviderOperationsAuthorization>();
builder.Services
    .AddOptions<ProviderDeliveryOptions>()
    .Bind(builder.Configuration.GetSection(ProviderDeliveryOptions.SectionName))
    .Validate(
        options => options.IsValid(builder.Environment.EnvironmentName),
        "Provider delivery settings are invalid; enabled delivery requires a bounded HTTPS endpoint and authorization.")
    .ValidateOnStart();
builder.Services.AddDbContext<AlertReceiverDbContext>(options =>
    options.UseNpgsql(
        connectionString,
        npgsql => npgsql.MigrationsHistoryTable(
            AlertReceiverDbContext.MigrationsHistoryTable,
            AlertReceiverDbContext.Schema)));
builder.Services.AddScoped<AvailabilityEventIngestionService>();
builder.Services.AddScoped<IProviderOutboxStore, EfProviderOutboxStore>();
builder.Services.AddSingleton<IProviderRetryDelayProvider, ExponentialProviderRetryDelayProvider>();
builder.Services.AddSingleton<IProviderDeliveryChannel, HttpProviderDeliveryChannel>();
builder.Services.AddScoped<ProviderDispatcher>();
builder.Services.AddScoped<ProviderDeliveryOperationsService>();
builder.Services.AddHostedService<ProviderDeliveryBackgroundService>();
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddMeter(ReceiverMetrics.MeterName)
        .AddPrometheusExporter());
builder.Services.AddHealthChecks()
    .AddDbContextCheck<AlertReceiverDbContext>(
        name: "database",
        tags: ["ready"]);

var app = builder.Build();

app.UseExceptionHandler();
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = static _ => false,
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = static healthCheck => healthCheck.Tags.Contains("ready"),
});
app.MapAvailabilityEventEndpoints();
app.MapProviderDeliveryOperationsEndpoints();
app.MapPrometheusScrapingEndpoint("/metrics");

app.Run();

public partial class Program;
