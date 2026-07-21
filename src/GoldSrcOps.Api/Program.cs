using GoldSrcOps.Api.Endpoints;
using GoldSrcOps.Api.Security;
using GoldSrcOps.Application.Commands;
using GoldSrcOps.Application.Credentials;
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
builder.Services.AddGoldSrcOpsSecurity(builder.Environment);
builder.Services.AddScoped<ServersService>();
builder.Services.AddScoped<IncidentsService>();
builder.Services.AddScoped<MonitoringReadService>();
builder.Services.AddScoped<ServerCredentialsService>();
builder.Services.AddScoped<CommandExecutionService>();
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
    app.MapOpenApi()
        .RequireAuthorization(GoldSrcOpsSecurity.ReaderPolicy);
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

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
app.MapServerEndpoints();
app.MapIncidentEndpoints();
app.MapDashboardEndpoints();
app.MapCommandEndpoints();

app.Run();

public partial class Program;
