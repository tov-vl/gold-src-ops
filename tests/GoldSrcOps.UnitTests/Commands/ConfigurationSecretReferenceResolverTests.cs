using AwesomeAssertions;
using GoldSrcOps.Infrastructure.Commands;
using Microsoft.Extensions.Configuration;

namespace GoldSrcOps.UnitTests.Commands;

public sealed class ConfigurationSecretReferenceResolverTests
{
    [Fact]
    public async Task ResolveAsync_reads_alias_from_dedicated_rcon_secrets_section()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["RconSecrets:primary_server"] = "secret-from-config",
                ["ConnectionStrings:GoldSrcOps"] = "database-secret"
            })
            .Build();
        var sut = new ConfigurationSecretReferenceResolver(configuration);

        var result = await sut.ResolveAsync("rcon-secret://primary_server", CancellationToken.None);

        result.Kind.Should().Be(SecretReferenceResolutionResultKind.Resolved);
        result.Secret.Should().Be("secret-from-config");
    }

    [Fact]
    public async Task ResolveAsync_reads_alias_from_environment_configuration_provider()
    {
        var secretAlias = $"server_{Guid.NewGuid():N}";
        var variableName = $"RconSecrets__{secretAlias}";
        Environment.SetEnvironmentVariable(variableName, "secret-from-environment");

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .Build();
            var sut = new ConfigurationSecretReferenceResolver(configuration);

            var result = await sut.ResolveAsync(
                GoldSrcOps.Domain.Servers.RconSecretReference.Create(secretAlias),
                CancellationToken.None);

            result.Kind.Should().Be(SecretReferenceResolutionResultKind.Resolved);
            result.Secret.Should().Be("secret-from-environment");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }

    [Fact]
    public async Task ResolveAsync_does_not_read_arbitrary_configuration_keys()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ConnectionStrings:GoldSrcOps"] = "database-secret"
            })
            .Build();
        var sut = new ConfigurationSecretReferenceResolver(configuration);

        var result = await sut.ResolveAsync(
            "config://ConnectionStrings:GoldSrcOps",
            CancellationToken.None);

        result.Kind.Should().Be(SecretReferenceResolutionResultKind.Unsupported);
        result.Secret.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_does_not_read_arbitrary_environment_variables()
    {
        var variableName = $"GOLDSRCOPS_TEST_SECRET_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(variableName, "environment-secret");

        try
        {
            var sut = new ConfigurationSecretReferenceResolver(new ConfigurationBuilder().Build());

            var result = await sut.ResolveAsync($"env://{variableName}", CancellationToken.None);

            result.Kind.Should().Be(SecretReferenceResolutionResultKind.Unsupported);
            result.Secret.Should().BeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }

    [Theory]
    [InlineData("dev-secrets://goldsrcops/server/rcon")]
    [InlineData("rcon-secret://ConnectionStrings:GoldSrcOps")]
    [InlineData("rcon-secret://nested/alias")]
    [InlineData("vault://goldsrcops/server/rcon")]
    public async Task ResolveAsync_returns_unsupported_for_non_alias_references(string secretReference)
    {
        var sut = new ConfigurationSecretReferenceResolver(new ConfigurationBuilder().Build());

        var result = await sut.ResolveAsync(secretReference, CancellationToken.None);

        result.Kind.Should().Be(SecretReferenceResolutionResultKind.Unsupported);
        result.Secret.Should().BeNull();
    }
}
