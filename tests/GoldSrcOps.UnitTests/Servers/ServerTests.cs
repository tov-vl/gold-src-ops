using GoldSrcOps.Domain.Servers;

namespace GoldSrcOps.UnitTests.Servers;

public sealed class ServerTests
{
    [Fact]
    public void UpdateDetails_replaces_editable_values_and_preserves_identity_state_and_created_time()
    {
        var createdAtUtc = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);
        var server = new Server(
            "Dust2 Public",
            GameServerKind.GoldSrc,
            new ServerEndpoint("127.0.0.1", queryPort: 27015, rconPort: null),
            pollIntervalSeconds: 30,
            notes: "before",
            createdAtUtc);
        var id = server.Id;
        var state = server.CurrentState;

        server.UpdateDetails(
            " Inferno Public ",
            new ServerEndpoint(" cs.example.local ", queryPort: 27016, rconPort: 27017),
            pollIntervalSeconds: 45,
            notes: " after ");

        Assert.Equal(id, server.Id);
        Assert.Equal("Inferno Public", server.Name);
        Assert.Equal(GameServerKind.GoldSrc, server.Game);
        Assert.Equal("cs.example.local", server.Endpoint.Host);
        Assert.Equal(27016, server.Endpoint.QueryPort);
        Assert.Equal(27017, server.Endpoint.RconPort);
        Assert.True(server.IsEnabled);
        Assert.Equal(45, server.PollIntervalSeconds);
        Assert.Equal("after", server.Notes);
        Assert.Equal(createdAtUtc, server.CreatedAtUtc);
        Assert.Same(state, server.CurrentState);
    }

    [Fact]
    public void UpdateDetails_clears_blank_notes()
    {
        var server = CreateServer();

        server.UpdateDetails(
            "Dust2 Public",
            new ServerEndpoint("127.0.0.1", queryPort: 27015, rconPort: null),
            pollIntervalSeconds: 30,
            notes: " ");

        Assert.Null(server.Notes);
    }

    [Fact]
    public void Enable_and_disable_update_polling_availability()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);
        var server = CreateServer();

        server.Disable();

        Assert.False(server.IsEnabled);
        Assert.False(server.IsDueForPolling(nowUtc));

        server.Enable();

        Assert.True(server.IsEnabled);
        Assert.True(server.IsDueForPolling(nowUtc));
    }

    [Fact]
    public void UpdateDetails_rejects_invalid_values()
    {
        var server = CreateServer();
        var endpoint = new ServerEndpoint("127.0.0.1", queryPort: 27015, rconPort: null);

        Assert.Throws<ArgumentException>(() => server.UpdateDetails(
            " ",
            endpoint,
            pollIntervalSeconds: 30,
            notes: null));
        Assert.Throws<ArgumentNullException>(() => server.UpdateDetails(
            "Dust2 Public",
            null!,
            pollIntervalSeconds: 30,
            notes: null));
        Assert.Throws<ArgumentOutOfRangeException>(() => server.UpdateDetails(
            "Dust2 Public",
            endpoint,
            pollIntervalSeconds: 0,
            notes: null));
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(65536, null)]
    [InlineData(27015, 0)]
    [InlineData(27015, 65536)]
    public void ServerEndpoint_rejects_ports_outside_tcp_range(int queryPort, int? rconPort)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ServerEndpoint("127.0.0.1", queryPort, rconPort));
    }

    private static Server CreateServer()
    {
        return new Server(
            "Dust2 Public",
            GameServerKind.GoldSrc,
            new ServerEndpoint("127.0.0.1", queryPort: 27015, rconPort: null),
            pollIntervalSeconds: 30,
            notes: "before",
            new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero));
    }
}
