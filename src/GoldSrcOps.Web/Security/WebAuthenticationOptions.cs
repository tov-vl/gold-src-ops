namespace GoldSrcOps.Web.Security;

internal sealed class WebAuthenticationOptions
{
    public const string SectionName = "Authentication";

    public bool Enabled { get; init; }

    public string? Authority { get; init; }

    public string? Audience { get; init; }

    public string? ClientId { get; init; }

    public string? ClientSecret { get; init; }

    public string? ClientSecretFile { get; init; }

    public string? RoleClaimType { get; init; }
}
