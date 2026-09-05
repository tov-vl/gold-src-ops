using GoldSrcOps.Api.Endpoints;
using GoldSrcOps.Api.Hosting;
using GoldSrcOps.Api.Security;
using GoldSrcOps.Application.Alerts;
using GoldSrcOps.Application.Commands;
using GoldSrcOps.Application.Credentials;
using GoldSrcOps.Application.Incidents;
using GoldSrcOps.Application.Monitoring;
using GoldSrcOps.Application.Servers;
using GoldSrcOps.Application.Telemetry;
using GoldSrcOps.Infrastructure;
using GoldSrcOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddJsonFile(
        "appsettings.Local.json",
        optional: true,
        reloadOnChange: true);
}

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddOutputCache(static options =>
    options.AddPolicy(
        PublicStatusEndpoints.CachePolicyName,
        static policy => policy.Expire(TimeSpan.FromSeconds(15))));
var reverseProxyEnabled = ReverseProxyConfiguration.Configure(
    builder.Services,
    builder.Configuration);
builder.Services.AddGoldSrcOpsSecurity(builder.Environment);
builder.Services.AddScoped<AlertDeliveryReadService>();
builder.Services.AddScoped<AlertDeliveryReplayService>();
builder.Services.AddScoped<ServersService>();
builder.Services.AddScoped<IncidentsService>();
builder.Services.AddScoped<MonitoringReadService>();
builder.Services.AddScoped<ServerCredentialsService>();
builder.Services.AddScoped<CommandExecutionService>();
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);
var otlpMetricsOptions = OtlpMetricsOptions.FromConfiguration(builder.Configuration);
builder.Services.AddHealthChecks()
    .AddDbContextCheck<GoldSrcOpsDbContext>(
        name: "database",
        tags: ["ready"]);
builder.Services.AddOpenTelemetry()
    .ConfigureResource(static resource => resource.AddService("GoldSrcOps"))
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddRuntimeInstrumentation()
        .AddMeter(GoldSrcOpsMetrics.MeterName)
        .AddPrometheusExporter()
        .AddConfiguredOtlpExporter(otlpMetricsOptions));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi()
        .RequireAuthorization(GoldSrcOpsSecurity.ReaderPolicy);
}

if (reverseProxyEnabled)
{
    app.UseForwardedHeaders();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseOutputCache();

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = static _ => false
})
    .AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = static healthCheck => healthCheck.Tags.Contains("ready")
})
    .AllowAnonymous();
app.MapPrometheusScrapingEndpoint("/metrics")
    .RequireAuthorization(GoldSrcOpsSecurity.ReaderPolicy);
app.MapPublicStatusEndpoints();
app.MapAlertDeliveryEndpoints();
app.MapServerEndpoints();
app.MapIncidentEndpoints();
app.MapDashboardEndpoints();
app.MapCommandEndpoints();

app.Run();

public partial class Program;
