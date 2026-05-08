using GoldSrcOps.Api.Endpoints;
using GoldSrcOps.Application.Incidents;
using GoldSrcOps.Application.Monitoring;
using GoldSrcOps.Application.Servers;
using GoldSrcOps.Application.Telemetry;
using GoldSrcOps.Infrastructure;
using GoldSrcOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddScoped<ServersService>();
builder.Services.AddScoped<IncidentsService>();
builder.Services.AddScoped<MonitoringReadService>();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddHealthChecks()
    .AddDbContextCheck<GoldSrcOpsDbContext>(
        name: "database",
        tags: ["ready"]);
builder.Services.AddOpenTelemetry()
    .ConfigureResource(static resource => resource.AddService("GoldSrcOps"))
    .WithMetrics(static metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddRuntimeInstrumentation()
        .AddMeter(GoldSrcOpsMetrics.MeterName)
        .AddPrometheusExporter());

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = static _ => false
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = static healthCheck => healthCheck.Tags.Contains("ready")
});
app.MapPrometheusScrapingEndpoint("/metrics");
app.MapServerEndpoints();
app.MapIncidentEndpoints();
app.MapDashboardEndpoints();

app.Run();

public partial class Program;
