using GoldSrcOps.Domain.Servers;

namespace GoldSrcOps.UnitTests.Credentials;

public sealed class ServerCredentialTests
{
    [Fact]
    public void Constructor_trims_secret_reference_and_marks_credential_configured()
    {
        var serverId = Guid.NewGuid();
        var createdAtUtc = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);

        var credential = new ServerCredential(
            serverId,
            ServerCredentialKind.RconPassword,
            " dev-secrets://goldsrcops/server-1/rcon ",
            createdAtUtc);

        Assert.NotEqual(Guid.Empty, credential.Id);
        Assert.Equal(serverId, credential.ServerId);
        Assert.Equal(ServerCredentialKind.RconPassword, credential.Kind);
        Assert.Equal("dev-secrets://goldsrcops/server-1/rcon", credential.SecretReference);
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
            "dev-secrets://old",
            new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero));
        var updatedAtUtc = new DateTimeOffset(2026, 4, 25, 12, 30, 0, TimeSpan.Zero);

        credential.UpdateSecretReference(" dev-secrets://new ", updatedAtUtc);

        Assert.Equal("dev-secrets://new", credential.SecretReference);
        Assert.Equal(updatedAtUtc, credential.UpdatedAtUtc);
    }

    [Fact]
    public void Constructor_rejects_missing_secret_reference()
    {
        Assert.Throws<ArgumentException>(() => new ServerCredential(
            Guid.NewGuid(),
            ServerCredentialKind.RconPassword,
            " ",
            DateTimeOffset.UtcNow));
    }
}
