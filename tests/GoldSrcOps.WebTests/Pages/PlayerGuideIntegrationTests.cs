using System.Net;
using AwesomeAssertions;
using GoldSrcOps.Contracts.Monitoring;

namespace GoldSrcOps.WebTests.Pages;

public sealed class PlayerGuideIntegrationTests
{
    [Fact]
    public async Task Player_guide_is_anonymous_and_uses_only_the_public_join_projection()
    {
        await using var factory = new PublicDashboardWebApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/play");
        var body = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.Handler.RequestedPaths.Should().Equal("/api/public/server");
        body.Should().Contain("lang=\"ru\"");
        body.Should().Contain("Сервер отвечает");
        body.Should().Contain("de_dust2");
        body.Should().Contain("4 / 20");
        body.Should().Contain("steam://connect/play.example.test:27015");
        body.Should().Contain("connect play.example.test:27015");
        body.Should().Contain("/menu").And.Contain("/guns").And.Contain("/profile").And.Contain("/settings");
        body.Should().Contain("при следующем возрождении");
        body.Should().NotContain(PublicDashboardWebApplicationFactory.PrivateDataSentinel);
        body.Should().NotContain("/operator/");
    }

    [Theory]
    [InlineData("offline", "Сервер не отвечает")]
    [InlineData("unknown", "Свежий статус не подтвержден")]
    [InlineData("unexpected", "Свежий статус не подтвержден")]
    public async Task Player_guide_does_not_present_old_map_and_population_as_current(
        string state,
        string expectedLabel)
    {
        await using var factory = new PublicDashboardWebApplicationFactory(
            serverJoinResponse: Server(state));
        using var client = factory.CreateClient();

        var body = WebUtility.HtmlDecode(await client.GetStringAsync("/play"));

        body.Should().Contain(expectedLabel);
        body.Should().Contain("Нет свежих данных");
        body.Should().NotContain("stale-map-must-not-render");
        body.Should().NotContain("17 / 31");
        body.Should().Contain("Попробовать подключиться");
        body.Should().Contain("connect play.example.test:27015");
        body.Should().Contain("2026-09-07T12:00:00.0000000+00:00");
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Player_guide_retains_instructions_without_inventing_a_connection_address(
        HttpStatusCode failureStatus)
    {
        await using var factory = new PublicDashboardWebApplicationFactory(
            serverJoinFailureStatus: failureStatus);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/play");
        var body = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("Подключение пока недоступно");
        body.Should().Contain("Три шага до первого боя");
        body.Should().Contain("/profile");
        body.Should().NotContain("steam://connect/");
        body.Should().NotContain("data-copy-connect");
        body.Should().NotContain(PublicDashboardWebApplicationFactory.PrivateDataSentinel);
    }

    [Fact]
    public async Task Player_guide_encodes_advertised_name_and_map_instead_of_rendering_markup()
    {
        var server = Server("online") with
        {
            Name = "<script>window.nameInjected=true</script>",
            Map = "<img src=x onerror=window.mapInjected=true>"
        };
        await using var factory = new PublicDashboardWebApplicationFactory(serverJoinResponse: server);
        using var client = factory.CreateClient();

        var body = await client.GetStringAsync("/play");

        body.Should().Contain("&lt;script&gt;");
        body.Should().Contain("&lt;img");
        body.Should().NotContain(server.Name);
        body.Should().NotContain(server.Map);
    }

    internal static PublicServerJoinResponse Server(string state) => new(
        "GoldSrcOps CS 1.6",
        "play.example.test",
        27015,
        state,
        "stale-map-must-not-render",
        17,
        31,
        new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero));
}
