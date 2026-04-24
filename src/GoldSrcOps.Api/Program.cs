using GoldSrcOps.Api.Endpoints;
using GoldSrcOps.Application.Incidents;
using GoldSrcOps.Application.Servers;
using GoldSrcOps.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddScoped<ServersService>();
builder.Services.AddScoped<IncidentsService>();
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");
app.MapServerEndpoints();
app.MapIncidentEndpoints();

app.Run();
