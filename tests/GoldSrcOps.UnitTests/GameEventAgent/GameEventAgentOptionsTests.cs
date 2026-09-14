using AwesomeAssertions;
using GoldSrcOps.GameEventAgent;
using Microsoft.Extensions.Configuration;

namespace GoldSrcOps.UnitTests.GameEventAgent;

public sealed class GameEventAgentOptionsTests
{
    [Fact]
    public void FromConfiguration_keeps_delivery_disabled_without_remote_settings()
    {
        var root = Path.GetFullPath("agent-options-test-root");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();

        var result = GameEventAgentOptions.FromConfiguration(configuration, root);

        result.Delivery.Should().BeNull();
        result.Queue.DatabasePath.Should().Be(Path.Combine(root, "data", "game-event-agent.db"));
        result.Queue.Capacity.Should().Be(10_000);
    }

    [Fact]
    public void FromConfiguration_accepts_https_delivery_and_secret_file_path()
    {
        var root = Path.GetFullPath("agent-options-test-root");
        var configuration = BuildConfiguration(
            apiBaseUrl: "https://api.example.test",
            tokenEndpoint: "https://identity.example.test/oauth/token");

        var result = GameEventAgentOptions.FromConfiguration(configuration, root);

        result.Delivery.Should().NotBeNull();
        result.Delivery!.ApiBaseUri.AbsoluteUri.Should().Be("https://api.example.test/");
        result.Delivery.OAuth.ClientSecretFile.Should().Be(
            Path.Combine(root, "secrets", "client-secret"));
    }

    [Fact]
    public void FromConfiguration_rejects_cleartext_remote_endpoint()
    {
        var configuration = BuildConfiguration(
            apiBaseUrl: "http://api.example.test",
            tokenEndpoint: "https://identity.example.test/oauth/token");

        var act = () => GameEventAgentOptions.FromConfiguration(
            configuration,
            Path.GetFullPath("agent-options-test-root"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*GameEventAgent:Delivery:ApiBaseUrl*");
    }

    [Fact]
    public void FromConfiguration_rejects_lease_not_longer_than_request_timeout()
    {
        var configuration = BuildConfiguration(
            apiBaseUrl: "https://api.example.test",
            tokenEndpoint: "https://identity.example.test/oauth/token",
            leaseSeconds: "20");

        var act = () => GameEventAgentOptions.FromConfiguration(
            configuration,
            Path.GetFullPath("agent-options-test-root"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*GameEventAgent:Delivery:LeaseSeconds*");
    }

    private static IConfiguration BuildConfiguration(
        string apiBaseUrl,
        string tokenEndpoint,
        string leaseSeconds = "30") =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["GameEventAgent:Delivery:Enabled"] = "true",
                ["GameEventAgent:Delivery:ServerId"] = "bd790c72-30f1-4dd9-9ec3-b79da8315227",
                ["GameEventAgent:Delivery:ApiBaseUrl"] = apiBaseUrl,
                ["GameEventAgent:Delivery:LeaseSeconds"] = leaseSeconds,
                ["GameEventAgent:Delivery:RequestTimeoutSeconds"] = "10",
                ["GameEventAgent:Delivery:OAuth:TokenEndpoint"] = tokenEndpoint,
                ["GameEventAgent:Delivery:OAuth:ClientId"] = "sandbox-client",
                ["GameEventAgent:Delivery:OAuth:ClientSecretFile"] = "secrets/client-secret",
                ["GameEventAgent:Delivery:OAuth:Audience"] = "https://api.example.test"
            })
            .Build();
}
