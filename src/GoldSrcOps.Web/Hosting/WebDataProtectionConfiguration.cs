using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using GoldSrcOps.Web.Security;
using Microsoft.AspNetCore.DataProtection;

namespace GoldSrcOps.Web.Hosting;

internal static class WebDataProtectionConfiguration
{
    private const string ApplicationName = "GoldSrcOps.Web";

    public static void Configure(
        IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        bool authenticationEnabled)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var settings = configuration
            .GetSection(WebDataProtectionOptions.SectionName)
            .Get<WebDataProtectionOptions>() ?? new WebDataProtectionOptions();
        var dataProtection = services.AddDataProtection().SetApplicationName(ApplicationName);

        if (!settings.PersistKeys)
        {
            if (authenticationEnabled && !environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    "Authenticated Web hosting requires a persistent Data Protection key ring outside Development.");
            }

            return;
        }

        var keysPath = GetRequiredAbsolutePath(settings.KeysPath, "DataProtection:KeysPath");
        var certificatePath = GetRequiredAbsolutePath(
            settings.CertificatePath,
            "DataProtection:CertificatePath");
        var certificatePassword = SecretFileReader.ReadRequiredSecret(
            settings.CertificatePassword,
            settings.CertificatePasswordFile,
            environment.IsDevelopment(),
            "DataProtection:CertificatePassword");

        Directory.CreateDirectory(keysPath);

        X509Certificate2 certificate;
        try
        {
            certificate = X509CertificateLoader.LoadPkcs12FromFile(
                certificatePath,
                certificatePassword,
                X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            throw new InvalidOperationException(
                "The Data Protection certificate could not be loaded.",
                exception);
        }

        if (!certificate.HasPrivateKey)
        {
            certificate.Dispose();
            throw new InvalidOperationException(
                "The Data Protection certificate must contain a private key.");
        }

        services.AddSingleton(certificate);
        dataProtection
            .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
            .ProtectKeysWithCertificate(certificate);
    }

    private static string GetRequiredAbsolutePath(string? value, string settingName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Path.IsPathFullyQualified(value.Trim()))
        {
            throw new InvalidOperationException(
                $"Configuration value '{settingName}' must be an absolute path.");
        }

        return value.Trim();
    }
}
