using AwesomeAssertions;
using GoldSrcOps.Infrastructure.Commands;
using Microsoft.Extensions.Configuration;

namespace GoldSrcOps.UnitTests.Commands;

public sealed class ConfigurationSecretReferenceResolverTests
{
    [Fact]
    public async Task ResolveAsync_reads_env_references_from_environment_variables()
    {
        const string variableName = "GOLDSRCOPS_TEST_RCON_PASSWORD";
        Environment.SetEnvironmentVariable(variableName, "secret-from-env");

        try
        {
            var sut = new ConfigurationSecretReferenceResolver(new ConfigurationBuilder().Build());

            var result = await sut.ResolveAsync($"env://{variableName}", CancellationToken.None);

            result.Kind.Should().Be(SecretReferenceResolutionResultKind.Resolved);
            result.Secret.Should().Be("secret-from-env");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }

    [Fact]
    public async Task ResolveAsync_reads_dev_secret_references_from_configuration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["DevSecrets:goldsrcops:server:rcon"] = "secret-from-config"
            })
            .Build();
        var sut = new ConfigurationSecretReferenceResolver(configuration);

        var result = await sut.ResolveAsync("dev-secrets://goldsrcops/server/rcon", CancellationToken.None);

        result.Kind.Should().Be(SecretReferenceResolutionResultKind.Resolved);
        result.Secret.Should().Be("secret-from-config");
    }

    [Fact]
    public async Task ResolveAsync_returns_unsupported_for_unknown_scheme()
    {
        var sut = new ConfigurationSecretReferenceResolver(new ConfigurationBuilder().Build());

        var result = await sut.ResolveAsync("vault://goldsrcops/server/rcon", CancellationToken.None);

        result.Kind.Should().Be(SecretReferenceResolutionResultKind.Unsupported);
        result.Secret.Should().BeNull();
    }
}
