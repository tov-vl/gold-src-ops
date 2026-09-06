using AwesomeAssertions;
using GoldSrcOps.Web.Security;

namespace GoldSrcOps.WebTests.Security;

public sealed class SecretFileReaderTests
{
    [Fact]
    public void ReadRequiredSecret_accepts_direct_value_in_development()
    {
        var result = SecretFileReader.ReadRequiredSecret(
            "development-secret",
            null,
            allowDirectValue: true,
            "Authentication:ClientSecret");

        result.Should().Be("development-secret");
    }

    [Fact]
    public void ReadRequiredSecret_rejects_direct_value_outside_development()
    {
        var action = () => SecretFileReader.ReadRequiredSecret(
            "production-secret",
            null,
            allowDirectValue: false,
            "Authentication:ClientSecret");

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*file-backed outside Development*");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("direct", "secret.txt")]
    public void ReadRequiredSecret_requires_exactly_one_source(string? directValue, string? filePath)
    {
        var action = () => SecretFileReader.ReadRequiredSecret(
            directValue,
            filePath,
            allowDirectValue: true,
            "Authentication:ClientSecret");

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*exactly one*");
    }
}
