using GoldSrcOps.Web.Security;
using GoldSrcOps.Web.Services;

namespace GoldSrcOps.Web.Hosting;

internal static class ProviderOperationsClientConfiguration
{
    private const int MaxAuthorizationLength = 8192;

    public static bool Configure(
        IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var options = configuration
            .GetSection(ProviderOperationsClientOptions.SectionName)
            .Get<ProviderOperationsClientOptions>() ?? new ProviderOperationsClientOptions();
        if (!options.Enabled)
        {
            services.AddSingleton<IProviderOperationsClient, DisabledProviderOperationsClient>();
            return false;
        }

        if (!Uri.TryCreate(options.BaseUrl?.Trim(), UriKind.Absolute, out var baseAddress) ||
            (!string.Equals(baseAddress.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(baseAddress.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "ProviderOperations:BaseUrl must be an absolute HTTP or HTTPS URL when enabled.");
        }

        var authorization = SecretFileReader.ReadRequiredSecret(
            options.Authorization,
            options.AuthorizationFile,
            environment.IsDevelopment(),
            "ProviderOperations:Authorization");
        if (authorization.Length > MaxAuthorizationLength)
        {
            throw new InvalidOperationException(
                $"ProviderOperations:Authorization must not exceed {MaxAuthorizationLength} characters.");
        }

        services.AddHttpClient<IProviderOperationsClient, ProviderOperationsClient>(client =>
            ConfigureClient(client, baseAddress, authorization));

        return true;
    }

    internal static void ConfigureClient(
        HttpClient client,
        Uri baseAddress,
        string authorization)
    {
        client.BaseAddress = baseAddress;
        client.Timeout = TimeSpan.FromSeconds(10);
        if (!client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authorization))
        {
            throw new InvalidOperationException(
                "ProviderOperations:Authorization could not be configured.");
        }
    }
}

internal sealed class ProviderOperationsClientOptions
{
    public const string SectionName = "ProviderOperations";

    public bool Enabled { get; init; }

    public string? BaseUrl { get; init; }

    public string? Authorization { get; init; }

    public string? AuthorizationFile { get; init; }
}
