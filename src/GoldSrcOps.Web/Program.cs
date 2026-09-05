using GoldSrcOps.Web.Components;
using GoldSrcOps.Web.Hosting;
using GoldSrcOps.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents();
builder.Services.AddHealthChecks();
var reverseProxyEnabled = ReverseProxyConfiguration.Configure(
    builder.Services,
    builder.Configuration);
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
app.UseAntiforgery();
app.MapStaticAssets();
app.MapHealthChecks("/health/live")
    .AllowAnonymous();
app.MapRazorComponents<App>();

app.Run();
