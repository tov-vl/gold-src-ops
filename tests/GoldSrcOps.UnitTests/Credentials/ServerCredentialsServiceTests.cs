using AwesomeAssertions;
using GoldSrcOps.Application.Common;
using GoldSrcOps.Application.Credentials;
using GoldSrcOps.Domain.Servers;
using Moq;

namespace GoldSrcOps.UnitTests.Credentials;

public sealed class ServerCredentialsServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 10, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SetAsync_returns_not_found_before_mutation()
    {
        var repository = new Mock<IServerCredentialRepository>(MockBehavior.Strict);
        repository
            .Setup(x => x.GetServerForUpdateAsync(It.IsAny<Guid>(), CancellationToken.None))
            .ReturnsAsync((Server?)null);
        var service = CreateService(repository);

        var result = await service.SetAsync(
            Guid.NewGuid(),
            CreateCommand(),
            CancellationToken.None);

        result.Kind.Should().Be(SetServerCredentialResultKind.NotFound);
        result.Credential.Should().BeNull();
        repository.VerifyAll();
    }

    [Fact]
    public async Task SetAsync_requires_current_server_revision_and_paused_monitoring()
    {
        var pausedServer = CreateServer(isEnabled: false);
        var staleRepository = new Mock<IServerCredentialRepository>(MockBehavior.Strict);
        staleRepository
            .Setup(x => x.GetServerForUpdateAsync(pausedServer.Id, CancellationToken.None))
            .ReturnsAsync(pausedServer);
        var staleService = CreateService(staleRepository);

        var stale = await staleService.SetAsync(
            pausedServer.Id,
            CreateCommand(expectedServerRevision: 2),
            CancellationToken.None);

        stale.Kind.Should().Be(SetServerCredentialResultKind.RevisionConflict);
        staleRepository.VerifyAll();

        var activeServer = CreateServer(isEnabled: true);
        var activeRepository = new Mock<IServerCredentialRepository>(MockBehavior.Strict);
        activeRepository
            .Setup(x => x.GetServerForUpdateAsync(activeServer.Id, CancellationToken.None))
            .ReturnsAsync(activeServer);
        var activeService = CreateService(activeRepository);

        var active = await activeService.SetAsync(
            activeServer.Id,
            CreateCommand(),
            CancellationToken.None);

        active.Kind.Should().Be(SetServerCredentialResultKind.MonitoringEnabled);
        activeRepository.VerifyAll();
    }

    [Fact]
    public async Task SetAsync_rejects_incomplete_commands_before_reading_credential()
    {
        var server = CreateServer(isEnabled: false);
        var repository = new Mock<IServerCredentialRepository>(MockBehavior.Strict);
        repository
            .Setup(x => x.GetServerForUpdateAsync(server.Id, CancellationToken.None))
            .ReturnsAsync(server);
        repository
            .Setup(x => x.HasIncompleteCommandsAsync(server.Id, CancellationToken.None))
            .ReturnsAsync(true);
        var service = CreateService(repository);

        var result = await service.SetAsync(server.Id, CreateCommand(), CancellationToken.None);

        result.Kind.Should().Be(SetServerCredentialResultKind.CommandsInProgress);
        server.Revision.Should().Be(1);
        repository.VerifyAll();
    }

    [Fact]
    public async Task SetAsync_creates_sanitized_metadata_and_advances_server_revision()
    {
        var server = CreateServer(isEnabled: false);
        ServerCredential? added = null;
        var repository = CreateMutationRepository(server);
        repository
            .Setup(x => x.GetAsync(server.Id, ServerCredentialKind.RconPassword, CancellationToken.None))
            .ReturnsAsync((ServerCredential?)null);
        repository
            .Setup(x => x.AddAsync(It.IsAny<ServerCredential>(), CancellationToken.None))
            .Callback<ServerCredential, CancellationToken>((credential, _) => added = credential)
            .Returns(Task.CompletedTask);
        repository
            .Setup(x => x.TrySaveChangesAsync(CancellationToken.None))
            .ReturnsAsync(true);
        var service = CreateService(repository);

        var result = await service.SetAsync(server.Id, CreateCommand(), CancellationToken.None);

        result.Kind.Should().Be(SetServerCredentialResultKind.Updated);
        result.Credential.Should().BeEquivalentTo(new
        {
            ServerId = server.Id,
            Revision = 1L,
            Kind = ServerCredentialKind.RconPassword,
            IsConfigured = true,
            CreatedAtUtc = Now,
            UpdatedAtUtc = (DateTimeOffset?)null
        });
        server.Revision.Should().Be(2);
        added!.SecretReference.Should().Be("rcon-secret://primary.server_1");
        repository.VerifyAll();
    }

    [Fact]
    public async Task SetAsync_rotates_current_revision_and_reports_persistence_race()
    {
        var server = CreateServer(isEnabled: false);
        var credential = new ServerCredential(
            server.Id,
            ServerCredentialKind.RconPassword,
            "rcon-secret://old",
            Now.AddHours(-1));
        var repository = CreateMutationRepository(server);
        repository
            .Setup(x => x.GetAsync(server.Id, ServerCredentialKind.RconPassword, CancellationToken.None))
            .ReturnsAsync(credential);
        repository
            .Setup(x => x.TrySaveChangesAsync(CancellationToken.None))
            .ReturnsAsync(false);
        var service = CreateService(repository);

        var result = await service.SetAsync(
            server.Id,
            CreateCommand(expectedCredentialRevision: 1),
            CancellationToken.None);

        result.Kind.Should().Be(SetServerCredentialResultKind.RevisionConflict);
        credential.Revision.Should().Be(2);
        server.Revision.Should().Be(2);
        repository.VerifyAll();
    }

    [Fact]
    public async Task SetAsync_rejects_stale_credential_revision_without_mutation()
    {
        var server = CreateServer(isEnabled: false);
        var credential = new ServerCredential(
            server.Id,
            ServerCredentialKind.RconPassword,
            "rcon-secret://old",
            Now.AddHours(-1));
        var repository = CreateMutationRepository(server);
        repository
            .Setup(x => x.GetAsync(server.Id, ServerCredentialKind.RconPassword, CancellationToken.None))
            .ReturnsAsync(credential);
        var service = CreateService(repository);

        var result = await service.SetAsync(
            server.Id,
            CreateCommand(expectedCredentialRevision: 2),
            CancellationToken.None);

        result.Kind.Should().Be(SetServerCredentialResultKind.RevisionConflict);
        credential.Revision.Should().Be(1);
        server.Revision.Should().Be(1);
        repository.VerifyAll();
    }

    private static Mock<IServerCredentialRepository> CreateMutationRepository(Server server)
    {
        var repository = new Mock<IServerCredentialRepository>(MockBehavior.Strict);
        repository
            .Setup(x => x.GetServerForUpdateAsync(server.Id, CancellationToken.None))
            .ReturnsAsync(server);
        repository
            .Setup(x => x.HasIncompleteCommandsAsync(server.Id, CancellationToken.None))
            .ReturnsAsync(false);
        return repository;
    }

    private static ServerCredentialsService CreateService(
        Mock<IServerCredentialRepository> repository)
    {
        var clock = new Mock<IClock>(MockBehavior.Strict);
        clock.SetupGet(x => x.UtcNow).Returns(Now);
        return new ServerCredentialsService(repository.Object, clock.Object);
    }

    private static SetServerCredentialCommand CreateCommand(
        long expectedServerRevision = 1,
        long expectedCredentialRevision = 0) =>
        new(
            ServerCredentialKind.RconPassword,
            " Primary.Server_1 ",
            expectedServerRevision,
            expectedCredentialRevision);

    private static Server CreateServer(bool isEnabled) =>
        new(
            "Server",
            GameServerKind.GoldSrc,
            new ServerEndpoint("game.example.test", 27015, 27015),
            pollIntervalSeconds: 30,
            notes: null,
            Now,
            isEnabled);
}
