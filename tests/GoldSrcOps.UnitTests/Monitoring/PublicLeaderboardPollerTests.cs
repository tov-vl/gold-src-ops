using AwesomeAssertions;
using GoldSrcOps.Application.Common;
using GoldSrcOps.Application.Credentials;
using GoldSrcOps.Application.Monitoring;
using GoldSrcOps.Application.Servers;
using GoldSrcOps.Domain.Servers;
using GoldSrcOps.Infrastructure.Commands;
using GoldSrcOps.Infrastructure.Monitoring;
using Moq;

namespace GoldSrcOps.UnitTests.Monitoring;

public sealed class PublicLeaderboardPollerTests
{
    [Theory]
    [InlineData("ready")]
    [InlineData("busy")]
    [InlineData("disabled")]
    [InlineData("missing-server")]
    [InlineData("missing-port")]
    [InlineData("missing-credential")]
    [InlineData("missing-secret")]
    [InlineData("unavailable")]
    [InlineData("cancel")]
    public async Task Poller_is_bound_to_configured_server_and_does_not_write_or_queue_commands(string scenario)
    {
        var now = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        var server = new Server("Fixture", GameServerKind.GoldSrc,
            new("127.0.0.1", 27015, string.Equals(scenario, "missing-port", StringComparison.Ordinal) ? null : 27015),
            60, null, now);
        var servers = new Mock<IServerRepository>(MockBehavior.Strict);
        var credentials = new Mock<IServerCredentialRepository>(MockBehavior.Strict);
        var secrets = new FixtureSecrets(scenario);
        var rcon = new FixtureRcon(scenario);
        var clock = new Mock<IClock>();
        clock.SetupGet(x => x.UtcNow).Returns(now);
        var store = new PublicLeaderboardStore(clock.Object);
        store.Publish(new(now, [new(1, "prior", 0, 1, 0)]));
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        servers.Setup(x => x.GetAsync(server.Id, token)).ReturnsAsync(
            string.Equals(scenario, "missing-server", StringComparison.Ordinal) ? null : server);
        credentials.Setup(x => x.HasIncompleteCommandsAsync(server.Id, token))
            .ReturnsAsync(string.Equals(scenario, "busy", StringComparison.Ordinal));
        credentials.Setup(x => x.GetAsync(server.Id, ServerCredentialKind.RconPassword, token)).ReturnsAsync(
            string.Equals(scenario, "missing-credential", StringComparison.Ordinal) ? null :
            new ServerCredential(server.Id, ServerCredentialKind.RconPassword, "rcon-secret://fixture", now));
        var settings = new PublicLeaderboardSettings(!string.Equals(scenario, "disabled", StringComparison.Ordinal), server.Id);
        var poller = new PublicLeaderboardPoller(settings, servers.Object, credentials.Object, secrets, rcon, store, clock.Object);
        if (string.Equals(scenario, "cancel", StringComparison.Ordinal))
        {
            var action = () => poller.PollAsync(token);
            await action.Should().ThrowAsync<OperationCanceledException>();
        }
        else
        {
            await poller.PollAsync(token);
            store.Read().State.Should().Be(scenario switch
            {
                "ready" or "disabled" => "fresh",
                "busy" => "stale",
                _ => "unavailable"
            });
        }

        if (rcon.LastRequest is { } request)
        {
            request.Should().BeEquivalentTo(new GoldSrcRconRequest(server.Endpoint.Host, 27015, "fixture-secret",
                "goldsrcops_leaderboard_snapshot", TimeSpan.FromSeconds(3)));
            rcon.LastToken.Should().Be(token);
        }

        servers.Verify(x => x.TrySaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        credentials.Verify(x => x.TrySaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        if (scenario is not ("ready" or "unavailable" or "cancel"))
        {
            rcon.LastRequest.Should().BeNull();
        }
    }
    private sealed class FixtureSecrets(string scenario) : ISecretReferenceResolver
    {
        public Task<SecretReferenceResolutionResult> ResolveAsync(string secretReference, CancellationToken cancellationToken)
        {
            secretReference.Should().Be("rcon-secret://fixture");
            return Task.FromResult(string.Equals(scenario, "missing-secret", StringComparison.Ordinal)
                ? SecretReferenceResolutionResult.NotFound() : SecretReferenceResolutionResult.Resolved("fixture-secret"));
        }
    }

    private sealed class FixtureRcon(string scenario) : IGoldSrcRconClient
    {
        public GoldSrcRconRequest? LastRequest { get; private set; }
        public CancellationToken LastToken { get; private set; }

        public Task<string> ExecuteAsync(GoldSrcRconRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastToken = cancellationToken;
            return string.Equals(scenario, "cancel", StringComparison.Ordinal)
                ? Task.FromCanceled<string>(new CancellationToken(canceled: true))
                : Task.FromResult(string.Equals(scenario, "unavailable", StringComparison.Ordinal)
                    ? "GSLEADER 1 unavailable" : PublicLeaderboardTests.Frame("GSLROW 1 0 2 0 41\n"));
        }
    }

}
