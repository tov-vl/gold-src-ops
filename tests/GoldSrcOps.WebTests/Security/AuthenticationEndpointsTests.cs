using AwesomeAssertions;
using GoldSrcOps.Web.Endpoints;

namespace GoldSrcOps.WebTests.Security;

public sealed class AuthenticationEndpointsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://example.test/operator/servers")]
    [InlineData("//example.test/operator/servers")]
    [InlineData("/\\example.test/operator/servers")]
    [InlineData("/operator/servers\r\nLocation: https://example.test")]
    public void GetLocalReturnUrl_replaces_non_local_values(string? returnUrl)
    {
        var result = AuthenticationEndpoints.GetLocalReturnUrl(returnUrl, "/operator/servers");

        result.Should().Be("/operator/servers");
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/operator/servers")]
    [InlineData("/operator/servers?view=enabled")]
    public void GetLocalReturnUrl_preserves_local_values(string returnUrl)
    {
        var result = AuthenticationEndpoints.GetLocalReturnUrl(returnUrl, "/operator/servers");

        result.Should().Be(returnUrl);
    }
}
