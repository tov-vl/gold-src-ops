using GoldSrcOps.Web.Components;
using GoldSrcOps.Web.Endpoints;
using GoldSrcOps.Web.Hosting;
using GoldSrcOps.Web.Security;
using GoldSrcOps.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHealthChecks();
var reverseProxyEnabled = ReverseProxyConfiguration.Configure(
    builder.Services,
    builder.Configuration);
var authenticationEnabled = WebSecurityConfiguration.Configure(
    builder.Services,
    builder.Configuration,
    builder.Environment);
WebDataProtectionConfiguration.Configure(
    builder.Services,
    builder.Configuration,
    builder.Environment,
    authenticationEnabled);
var apiBaseUrl = builder.Configuration["GoldSrcOpsApi:BaseUrl"];
if (!Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var apiBaseAddress) ||
    (!string.Equals(apiBaseAddress.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
     !string.Equals(apiBaseAddress.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
{
    throw new InvalidOperationException("GoldSrcOpsApi:BaseUrl must be an absolute HTTP or HTTPS URL.");
}

builder.Services.AddHttpClient<PublicStatusClient>(client =>
{
    client.BaseAddress = apiBaseAddress;
    client.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddScoped<AccessTokenHandler>();
builder.Services.AddHttpClient<IReaderApiClient, ReaderApiClient>(client =>
{
    client.BaseAddress = apiBaseAddress;
    client.Timeout = TimeSpan.FromSeconds(10);
})
    .AddHttpMessageHandler<AccessTokenHandler>();
builder.Services.AddHttpClient<IOperatorApiClient, OperatorApiClient>(client =>
{
    client.BaseAddress = apiBaseAddress;
    client.Timeout = TimeSpan.FromSeconds(10);
})
    .AddHttpMessageHandler<AccessTokenHandler>();

var app = builder.Build();

if (reverseProxyEnabled)
{
    app.UseForwardedHeaders();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapHealthChecks("/health/live")
    .AllowAnonymous();
app.MapAuthenticationEndpoints(authenticationEnabled);
app.MapOperatorCommandEndpoints();
app.MapOperatorRconCredentialEndpoints();
app.MapOperatorReplayEndpoints();
app.MapOperatorServerMonitoringEndpoints();
app.MapOperatorServerRegistrationEndpoints();
app.MapOperatorServerUpdateEndpoints();
app.MapRazorComponents<App>();

app.Run();

public partial class Program;
