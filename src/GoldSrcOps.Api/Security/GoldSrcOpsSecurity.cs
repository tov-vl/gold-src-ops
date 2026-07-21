using System.Security.Claims;
using GoldSrcOps.Domain.Commands;

namespace GoldSrcOps.Api.Security;

internal static class GoldSrcOpsSecurity
{
    public const string ReaderPolicy = "Reader";
    public const string OperatorPolicy = "Operator";
    public const string ReaderRole = "Reader";
    public const string OperatorRole = "Operator";
    public const string SubjectClaimType = "sub";

    public static string GetRequiredSubject(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (!TryGetSubject(principal, out var subject))
        {
            throw new InvalidOperationException(
                "The authenticated principal does not contain a valid subject.");
        }

        return subject;
    }

    public static bool TryGetSubject(ClaimsPrincipal? principal, out string subject)
    {
        subject = string.Empty;

        var candidate = principal?.FindFirst(SubjectClaimType)?.Value ??
            principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var normalized = candidate.Trim();
        if (normalized.Length > CommandExecution.MaxRequestedByLength)
        {
            return false;
        }

        subject = normalized;
        return true;
    }
}
