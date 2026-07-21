using AwesomeAssertions;
using GoldSrcOps.Application.Commands;
using GoldSrcOps.Domain.Commands;
using GoldSrcOps.Infrastructure.Commands;
using Microsoft.Extensions.Logging.Abstractions;

namespace GoldSrcOps.UnitTests.Commands;

public sealed class GoldSrcRconCommandExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_resolves_secret_and_executes_rcon_command()
    {
        var resolver = new StubSecretReferenceResolver(SecretReferenceResolutionResult.Resolved("super-secret"));
        var client = new StubGoldSrcRconClient("Executed with super-secret");
        var sut = CreateSut(resolver, client);
        var request = CreateRequest();

        var result = await sut.ExecuteAsync(request, CancellationToken.None);

        result.Kind.Should().Be(RconCommandExecutionResultKind.Succeeded);
        result.ResultSummary.Should().Be("Executed with [credential]");
        result.FailureReason.Should().BeNull();
        client.LastRequest.Should().BeEquivalentTo(new
        {
            Host = "127.0.0.1",
            Port = 27015,
            Password = "super-secret",
            CommandText = "say hello",
            Timeout = TimeSpan.FromSeconds(3)
        });
    }

    [Fact]
    public async Task ExecuteAsync_fails_without_calling_client_when_secret_is_missing()
    {
        var resolver = new StubSecretReferenceResolver(SecretReferenceResolutionResult.NotFound());
        var client = new StubGoldSrcRconClient("should not execute");
        var sut = CreateSut(resolver, client);

        var result = await sut.ExecuteAsync(CreateRequest(), CancellationToken.None);

        result.Kind.Should().Be(RconCommandExecutionResultKind.Failed);
        result.FailureReason.Should().Be("RCON credential secret could not be resolved.");
        client.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_maps_authentication_failure_to_safe_failure_reason()
    {
        var resolver = new StubSecretReferenceResolver(SecretReferenceResolutionResult.Resolved("super-secret"));
        var client = new StubGoldSrcRconClient(_ => throw new GoldSrcRconAuthenticationException());
        var sut = CreateSut(resolver, client);

        var result = await sut.ExecuteAsync(CreateRequest(), CancellationToken.None);

        result.Kind.Should().Be(RconCommandExecutionResultKind.AuthenticationFailed);
        result.FailureReason.Should().Be("RCON authentication failed.");
        result.FailureReason.Should().NotContain("super-secret");
    }

    private static GoldSrcRconCommandExecutor CreateSut(
        ISecretReferenceResolver resolver,
        IGoldSrcRconClient client) =>
        new(
            resolver,
            client,
            new GoldSrcRconOptions(),
            NullLogger<GoldSrcRconCommandExecutor>.Instance);

    private static RconCommandExecutionRequest CreateRequest() =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "127.0.0.1",
            27015,
            "rcon-secret://server_rcon",
            ServerCommandType.Say,
            "say hello");

    private sealed class StubSecretReferenceResolver : ISecretReferenceResolver
    {
        private readonly SecretReferenceResolutionResult _result;

        public StubSecretReferenceResolver(SecretReferenceResolutionResult result)
        {
            _result = result;
        }

        public Task<SecretReferenceResolutionResult> ResolveAsync(
            string secretReference,
            CancellationToken cancellationToken) =>
            Task.FromResult(_result);
    }

    private sealed class StubGoldSrcRconClient : IGoldSrcRconClient
    {
        private readonly Func<GoldSrcRconRequest, Task<string>> _execute;

        public StubGoldSrcRconClient(string result)
            : this(_ => Task.FromResult(result))
        {
        }

        public StubGoldSrcRconClient(Func<GoldSrcRconRequest, string> execute)
            : this(request => Task.FromResult(execute(request)))
        {
        }

        private StubGoldSrcRconClient(Func<GoldSrcRconRequest, Task<string>> execute)
        {
            _execute = execute;
        }

        public GoldSrcRconRequest? LastRequest { get; private set; }

        public int CallCount { get; private set; }

        public async Task<string> ExecuteAsync(GoldSrcRconRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;

            return await _execute(request);
        }
    }
}
