using GoldSrcOps.Web.Components;
using GoldSrcOps.Web.Endpoints;
using GoldSrcOps.Web.Hosting;
using GoldSrcOps.Web.Security;
using GoldSrcOps.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHealthChecks();
PublicPlayerSearchConfiguration.Configure(builder.Services, builder.Configuration);
WebTelemetryConfiguration.Configure(builder.Services, builder.Configuration);
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
builder.Services.AddHttpClient<PublicPlayerSearchClient>(client =>
{
    client.BaseAddress = apiBaseAddress;
    client.Timeout = TimeSpan.FromSeconds(5);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddScoped<AccessTokenHandler>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient<ICredentialExpiryClient, CredentialExpiryClient>(client =>
{
    client.BaseAddress = apiBaseAddress;
    client.Timeout = TimeSpan.FromSeconds(7);
    client.MaxResponseContentBufferSize = 16384;
}).ConfigurePrimaryHttpMessageHandler(static () => new HttpClientHandler { AllowAutoRedirect = false })
    .AddHttpMessageHandler<AccessTokenHandler>();
builder.Services.AddSingleton(new GameHostHealthLinks(builder.Configuration["GameHostHealth:GrafanaBaseUrl"]));
builder.Services.AddHttpClient<IPollingHealthClient, PollingHealthClient>(client =>
{
    client.BaseAddress = apiBaseAddress;
    client.Timeout = TimeSpan.FromSeconds(7);
    client.MaxResponseContentBufferSize = 16384;
}).ConfigurePrimaryHttpMessageHandler(static () => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    .AddHttpMessageHandler<AccessTokenHandler>();
builder.Services.AddHttpClient<IRetentionHealthClient, RetentionHealthClient>(client =>
{
    client.BaseAddress = apiBaseAddress;
    client.Timeout = TimeSpan.FromSeconds(7);
    client.MaxResponseContentBufferSize = 16384;
}).ConfigurePrimaryHttpMessageHandler(static () => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    .AddHttpMessageHandler<AccessTokenHandler>();
builder.Services.AddHttpClient<ICommandDispatchHealthClient, CommandDispatchHealthClient>(client =>
{
    client.BaseAddress = apiBaseAddress;
    client.Timeout = TimeSpan.FromSeconds(7);
    client.MaxResponseContentBufferSize = 16384;
}).ConfigurePrimaryHttpMessageHandler(static () => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    .AddHttpMessageHandler<AccessTokenHandler>();
builder.Services.AddHttpClient<IArchiveHealthClient, ArchiveHealthClient>(client =>
{
    client.BaseAddress = apiBaseAddress;
    client.Timeout = TimeSpan.FromSeconds(7);
    client.MaxResponseContentBufferSize = 16384;
}).ConfigurePrimaryHttpMessageHandler(static () => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    .AddHttpMessageHandler<AccessTokenHandler>();
builder.Services.AddHttpClient<IGameHostHealthClient, GameHostHealthClient>(client =>
{
    client.BaseAddress = apiBaseAddress;
    client.Timeout = TimeSpan.FromSeconds(7);
    client.MaxResponseContentBufferSize = 65536;
}).ConfigurePrimaryHttpMessageHandler(static () => new HttpClientHandler { AllowAutoRedirect = false })
    .AddHttpMessageHandler<AccessTokenHandler>();
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
ProviderOperationsClientConfiguration.Configure(
    builder.Services,
    builder.Configuration,
    builder.Environment);
builder.Services.AddScoped<IIncidentDeliveryHealthClient, IncidentDeliveryHealthClient>();

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
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments(new PathString(OperatorHealthOverview.Path), StringComparison.OrdinalIgnoreCase) ||
        context.Request.Path.StartsWithSegments(new PathString("/operator/polling-health"), StringComparison.OrdinalIgnoreCase) ||
        context.Request.Path.StartsWithSegments(new PathString("/operator/retention-health"), StringComparison.OrdinalIgnoreCase) ||
        context.Request.Path.StartsWithSegments(new PathString("/operator/command-dispatch-health"), StringComparison.OrdinalIgnoreCase) ||
        context.Request.Path.StartsWithSegments(new PathString("/operator/archive-health"), StringComparison.OrdinalIgnoreCase) ||
        context.Request.Path.StartsWithSegments(new PathString("/operator/credential-expiry"), StringComparison.OrdinalIgnoreCase) ||
        context.Request.Path.Equals(new PathString("/operator/game-host-health"), StringComparison.OrdinalIgnoreCase) ||
        context.Request.Path.StartsWithSegments(new PathString(OperatorHealthReportEndpoints.Path), StringComparison.OrdinalIgnoreCase))
    {
        context.Response.Headers.CacheControl = "no-store";
    }

    await next(context).ConfigureAwait(false);
});
app.UseAuthorization();
app.UsePublicPlayerSearchBoundary();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapHealthChecks("/health/live")
    .AllowAnonymous();
app.MapAuthenticationEndpoints(authenticationEnabled);
app.MapOperatorCommandEndpoints();
app.MapOperatorMapChangeEndpoints();
app.MapOperatorRestartEndpoints();
app.MapOperatorRconCredentialEndpoints();
app.MapOperatorReplayEndpoints();
app.MapOperatorProviderReviewEndpoints();
app.MapOperatorServerMonitoringEndpoints();
app.MapOperatorServerRegistrationEndpoints();
app.MapOperatorServerUpdateEndpoints();
app.MapOperatorHealthReportEndpoints();
app.MapRazorComponents<App>();

app.Run();

public partial class Program;
