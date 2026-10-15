using System.Text;
using AwesomeAssertions;
using GoldSrcOps.Application.Credentials;
using GoldSrcOps.Application.Monitoring;
using GoldSrcOps.Application.Servers;
using GoldSrcOps.Domain.Servers;
using GoldSrcOps.Infrastructure.Commands;
using GoldSrcOps.Infrastructure.Monitoring;
using Moq;

namespace GoldSrcOps.UnitTests.Monitoring;

public sealed class PublicPlayerSearchSourceTests
{
    [Theory]
    [InlineData("ready", PublicPlayerSearchMode.Exact)]
    [InlineData("ready", PublicPlayerSearchMode.Contains)]
    [InlineData("disabled", PublicPlayerSearchMode.Exact)]
    [InlineData("disabled", PublicPlayerSearchMode.Contains)]
    [InlineData("invalid", PublicPlayerSearchMode.Exact)]
    [InlineData("invalid", PublicPlayerSearchMode.Contains)]
    [InlineData("missing-server", PublicPlayerSearchMode.Exact)]
    [InlineData("missing-server", PublicPlayerSearchMode.Contains)]
    [InlineData("disabled-server", PublicPlayerSearchMode.Exact)]
    [InlineData("disabled-server", PublicPlayerSearchMode.Contains)]
    [InlineData("missing-port", PublicPlayerSearchMode.Exact)]
    [InlineData("missing-port", PublicPlayerSearchMode.Contains)]
    [InlineData("operator-busy", PublicPlayerSearchMode.Exact)]
    [InlineData("operator-busy", PublicPlayerSearchMode.Contains)]
    [InlineData("missing-credential", PublicPlayerSearchMode.Exact)]
    [InlineData("missing-credential", PublicPlayerSearchMode.Contains)]
    [InlineData("missing-secret", PublicPlayerSearchMode.Exact)]
    [InlineData("missing-secret", PublicPlayerSearchMode.Contains)]
    [InlineData("unavailable", PublicPlayerSearchMode.Exact)]
    [InlineData("unavailable", PublicPlayerSearchMode.Contains)]
    [InlineData("old-addon", PublicPlayerSearchMode.Exact)]
    [InlineData("old-addon", PublicPlayerSearchMode.Contains)]
    [InlineData("cancel", PublicPlayerSearchMode.Exact)]
    [InlineData("cancel", PublicPlayerSearchMode.Contains)]
    [InlineData("wrong-version", PublicPlayerSearchMode.Exact)]
    [InlineData("wrong-version", PublicPlayerSearchMode.Contains)]
    public async Task Source_uses_only_configured_server_secret_and_fixed_hex_command(string scenario, PublicPlayerSearchMode mode)
    {
        var time = new SearchTime();
        var server = new Server("Fixture", GameServerKind.GoldSrc,
            new("127.0.0.1", 27015, string.Equals(scenario, "missing-port", StringComparison.Ordinal) ? null : 27016), 60, null, time.GetUtcNow());
        if (string.Equals(scenario, "disabled-server", StringComparison.Ordinal)) { server.Disable(); }
        var servers = new Mock<IServerRepository>(MockBehavior.Strict);
        var credentials = new Mock<IServerCredentialRepository>(MockBehavior.Strict);
        var secrets = new FixtureSecrets();
        var rcon = new FixtureRcon();
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        servers.Setup(x => x.GetAsync(server.Id, token)).ReturnsAsync(string.Equals(scenario, "missing-server", StringComparison.Ordinal) ? null : server);
        credentials.Setup(x => x.HasIncompleteCommandsAsync(server.Id, token)).ReturnsAsync(string.Equals(scenario, "operator-busy", StringComparison.Ordinal));
        credentials.Setup(x => x.GetAsync(server.Id, ServerCredentialKind.RconPassword, token)).ReturnsAsync(
            string.Equals(scenario, "missing-credential", StringComparison.Ordinal) ? null : new ServerCredential(server.Id, ServerCredentialKind.RconPassword, "rcon-secret://fixture", time.GetUtcNow()));
        secrets.Action = (reference, receivedToken) =>
        {
            reference.Should().Be("rcon-secret://fixture");
            receivedToken.Should().Be(token);
            return Task.FromResult(string.Equals(scenario, "missing-secret", StringComparison.Ordinal)
                ? SecretReferenceResolutionResult.NotFound() : SecretReferenceResolutionResult.Resolved("fixture-password"));
        };
        var name = string.Equals(scenario, "invalid", StringComparison.Ordinal) ? "A\nquit" : "Я;\"<&";
        var calls = 0;
        rcon.Action = (request, receivedToken) =>
        {
            receivedToken.Should().Be(token);
            calls++;
            request.Host.Should().Be(server.Endpoint.Host);
            request.Port.Should().Be(27016);
            request.Password.Should().Be("fixture-password");
            request.Timeout.Should().Be(TimeSpan.FromSeconds(3));
            var fields = request.CommandText.Split(' ');
            fields.Should().HaveCount(3);
            fields[0].Should().Be(mode == PublicPlayerSearchMode.Contains ? "goldsrcops_player_lookup_v2" : "goldsrcops_player_lookup");
            fields[1].Should().MatchRegex("^[0-9A-F]{16}$");
            fields[2].Should().Be(Convert.ToHexString(Encoding.UTF8.GetBytes(name)));
            return scenario switch
            {
                "cancel" => Task.FromCanceled<string>(new CancellationToken(true)),
                "unavailable" => Task.FromResult($"GSLOOKUP {(mode == PublicPlayerSearchMode.Contains ? 2 : 1)} {fields[1]} unavailable"),
                "wrong-version" => Task.FromResult(PublicPlayerSearchProtocolTests.Frame(nonce: fields[1], version: mode == PublicPlayerSearchMode.Contains ? 1 : 2)),
                "old-addon" => Task.FromResult("Unknown command: goldsrcops_player_lookup"),
                _ => Task.FromResult(PublicPlayerSearchProtocolTests.Frame(nonce: fields[1], version: mode == PublicPlayerSearchMode.Contains ? 2 : 1))
            };
        };
        var source = new PublicPlayerSearchSource(new(!string.Equals(scenario, "disabled", StringComparison.Ordinal), server.Id), servers.Object, credentials.Object, secrets, rcon, time);
        if (string.Equals(scenario, "cancel", StringComparison.Ordinal))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.LookupAsync(name, mode, token));
        }
        else if (scenario is "old-addon" or "wrong-version")
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => source.LookupAsync(name, mode, token));
        }
        else
        {
            var result = await source.LookupAsync(name, mode, token);
            result.State.Should().Be(scenario switch
            {
                "ready" => PublicPlayerSearchState.Fresh,
                "operator-busy" => PublicPlayerSearchState.Busy,
                _ => PublicPlayerSearchState.Unavailable
            });
        }
        calls.Should().Be(scenario is "ready" or "unavailable" or "old-addon" or "wrong-version" or "cancel" ? 1 : 0);
        servers.Verify(x => x.TrySaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        credentials.Verify(x => x.TrySaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        if (scenario is "disabled" or "invalid") { servers.Verify(x => x.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never); }
        if (string.Equals(scenario, "operator-busy", StringComparison.Ordinal)) { credentials.Verify(x => x.GetAsync(It.IsAny<Guid>(), It.IsAny<ServerCredentialKind>(), It.IsAny<CancellationToken>()), Times.Never); }
    }
    [Theory]
    [InlineData("ab", PublicPlayerSearchMode.Contains)]
    [InlineData("abc", (PublicPlayerSearchMode)99)]
    [InlineData(" abc ", PublicPlayerSearchMode.Contains)]
    public async Task Invalid_mode_or_noncanonical_query_does_not_resolve_private_resources(string name, PublicPlayerSearchMode mode)
    {
        var source = new PublicPlayerSearchSource(new(true, Guid.NewGuid()),
            new Mock<IServerRepository>(MockBehavior.Strict).Object,
            new Mock<IServerCredentialRepository>(MockBehavior.Strict).Object,
            new FixtureSecrets(), new FixtureRcon(), new SearchTime());
        (await source.LookupAsync(name, mode, default)).Should().Be(PublicPlayerSearchResult.Unavailable);
    }

    private sealed class FixtureSecrets : ISecretReferenceResolver
    {
        public Func<string, CancellationToken, Task<SecretReferenceResolutionResult>> Action { private get; set; } = null!;
        public Task<SecretReferenceResolutionResult> ResolveAsync(string reference, CancellationToken token) => Action(reference, token);
    }

    private sealed class FixtureRcon : IGoldSrcRconClient
    {
        public Func<GoldSrcRconRequest, CancellationToken, Task<string>> Action { private get; set; } = null!;
        public Task<string> ExecuteAsync(GoldSrcRconRequest request, CancellationToken token) => Action(request, token);
    }

}
