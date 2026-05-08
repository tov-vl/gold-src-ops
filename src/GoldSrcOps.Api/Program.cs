using GoldSrcOps.Api.Endpoints;
using GoldSrcOps.Application.Incidents;
using GoldSrcOps.Application.Monitoring;
using GoldSrcOps.Application.Servers;
using GoldSrcOps.Infrastructure;
using GoldSrcOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

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
app.MapServerEndpoints();
app.MapIncidentEndpoints();
app.MapDashboardEndpoints();

app.Run();

public partial class Program;
