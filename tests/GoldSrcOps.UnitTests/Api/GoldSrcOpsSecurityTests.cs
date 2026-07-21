using System.Security.Claims;
using GoldSrcOps.Api.Security;
using GoldSrcOps.Domain.Commands;

namespace GoldSrcOps.UnitTests.Api;

public sealed class GoldSrcOpsSecurityTests
{
    [Fact]
    public void TryGetSubject_reads_and_normalizes_sub_claim()
    {
        var principal = CreatePrincipal(new Claim(GoldSrcOpsSecurity.SubjectClaimType, " operator-42 "));

        var found = GoldSrcOpsSecurity.TryGetSubject(principal, out var subject);

        Assert.True(found);
        Assert.Equal("operator-42", subject);
    }

    [Fact]
    public void TryGetSubject_supports_framework_name_identifier_mapping()
    {
        var principal = CreatePrincipal(new Claim(ClaimTypes.NameIdentifier, "mapped-subject"));

        var found = GoldSrcOpsSecurity.TryGetSubject(principal, out var subject);

        Assert.True(found);
        Assert.Equal("mapped-subject", subject);
    }

    [Fact]
    public void TryGetSubject_rejects_oversized_subject()
    {
        var principal = CreatePrincipal(new Claim(
            GoldSrcOpsSecurity.SubjectClaimType,
            new string('x', CommandExecution.MaxRequestedByLength + 1)));

        var found = GoldSrcOpsSecurity.TryGetSubject(principal, out var subject);

        Assert.False(found);
        Assert.Empty(subject);
    }

    private static ClaimsPrincipal CreatePrincipal(Claim claim) =>
        new(new ClaimsIdentity([claim], authenticationType: "Test"));
}
