namespace GoldSrcOps.Web.Hosting;

internal sealed class WebDataProtectionOptions
{
    public const string SectionName = "DataProtection";

    public bool PersistKeys { get; init; }

    public string? KeysPath { get; init; }

    public string? CertificatePath { get; init; }

    public string? CertificatePassword { get; init; }

    public string? CertificatePasswordFile { get; init; }
}
