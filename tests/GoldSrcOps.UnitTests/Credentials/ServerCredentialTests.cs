using GoldSrcOps.Domain.Servers;

namespace GoldSrcOps.UnitTests.Credentials;

public sealed class ServerCredentialTests
{
    [Fact]
    public void Constructor_normalizes_canonical_secret_reference_and_marks_credential_configured()
    {
        var serverId = Guid.NewGuid();
        var createdAtUtc = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);

        var credential = new ServerCredential(
            serverId,
            ServerCredentialKind.RconPassword,
            " RCON-SECRET://Primary.Server_1 ",
            createdAtUtc);

        Assert.NotEqual(Guid.Empty, credential.Id);
        Assert.Equal(serverId, credential.ServerId);
        Assert.Equal(ServerCredentialKind.RconPassword, credential.Kind);
        Assert.Equal("rcon-secret://primary.server_1", credential.SecretReference);
        Assert.True(credential.IsConfigured);
        Assert.Equal(createdAtUtc, credential.CreatedAtUtc);
        Assert.Null(credential.UpdatedAtUtc);
    }

    [Fact]
    public void UpdateSecretReference_replaces_reference_and_tracks_update_time()
    {
        var credential = new ServerCredential(
            Guid.NewGuid(),
            ServerCredentialKind.RconPassword,
            "rcon-secret://old",
            new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero));
        var updatedAtUtc = new DateTimeOffset(2026, 4, 25, 12, 30, 0, TimeSpan.Zero);

        credential.UpdateSecretReference(" RCON-SECRET://New_Alias ", updatedAtUtc);

        Assert.Equal("rcon-secret://new_alias", credential.SecretReference);
        Assert.Equal(updatedAtUtc, credential.UpdatedAtUtc);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("env://GOLDSRCOPS_RCON_PASSWORD")]
    [InlineData("config://ConnectionStrings:GoldSrcOps")]
    [InlineData("dev-secrets://goldsrcops/server/rcon")]
    [InlineData("rcon-secret://ConnectionStrings:GoldSrcOps")]
    public void Constructor_rejects_missing_or_unsafe_secret_reference(string secretReference)
    {
        Assert.Throws<ArgumentException>(() => new ServerCredential(
            Guid.NewGuid(),
            ServerCredentialKind.RconPassword,
            secretReference,
            DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("primary-server")]
    [InlineData("primary.server_1")]
    [InlineData("A1")]
    public void Create_builds_canonical_reference_for_valid_alias(string secretAlias)
    {
        var secretReference = RconSecretReference.Create(secretAlias);

        Assert.Equal($"rcon-secret://{secretAlias.ToLowerInvariant()}", secretReference);
    }
}
