using System.Security.Claims;

namespace GoldSrcOps.Web.Security;

internal static class WebSecurity
{
    public const string ReaderPolicy = "Reader";
    public const string OperatorPolicy = "Operator";
    public const string ReaderRole = "Reader";
    public const string OperatorRole = "Operator";
    public const string SubjectClaim = "sub";

    public static string? GetSubject(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var subject = principal.FindFirstValue(SubjectClaim);
        return string.IsNullOrWhiteSpace(subject) ? null : subject;
    }
}
