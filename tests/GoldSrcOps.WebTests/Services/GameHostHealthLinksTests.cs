using AwesomeAssertions;
using GoldSrcOps.Web.Services;

namespace GoldSrcOps.WebTests.Services;

public sealed class GameHostHealthLinksTests
{
    [Theory]
    [InlineData("https://grafana.example.test/")]
    [InlineData("http://127.0.0.1:3000/")]
    public void Builds_only_fixed_dashboard_paths(string value)
    {
        var links = new GameHostHealthLinks(value);
        links.Dashboards.Should().HaveCount(6);
        links.Dashboards.Should().OnlyContain(pair => pair.Value.AbsoluteUri.StartsWith(value + "d/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("http://private.example/")]
    [InlineData("https://user:password@example.test/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://example.test/?token=value")]
    public void Rejects_unsafe_link_origins(string value)
    {
        var action = () => new GameHostHealthLinks(value);
        action.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("game-event-agent", "goldsrcops-game-event-agent-alerts")]
    [InlineData("player-backup", "goldsrcops-player-backup")]
    [InlineData("game-host-storage", "goldsrcops-game-host-storage")]
    [InlineData("game-host-resources", "goldsrcops-game-host-resources")]
    public void Warning_links_use_only_fixed_component_destinations(string component, string dashboard)
    {
        new GameHostHealthLinks("https://grafana.example.test/").WarningDashboard(component)!
            .AbsoluteUri.Should().Be("https://grafana.example.test/d/" + dashboard);
        new GameHostHealthLinks(baseUrl: null).WarningDashboard(component).Should().BeNull();
    }

    [Fact]
    public void Unknown_warning_components_cannot_supply_a_dashboard_url() =>
        new GameHostHealthLinks("https://grafana.example.test/").WarningDashboard("https://unexpected.test/").Should().BeNull();
}
