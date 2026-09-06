using System.Net;
using AwesomeAssertions;
using GoldSrcOps.Web.Security;

namespace GoldSrcOps.WebTests.Pages;

public sealed class ReaderPortalIntegrationTests
{
    [Fact]
    public async Task Protected_page_does_not_expose_data_when_authentication_is_disabled()
    {
        await using var factory = new DisabledAuthenticationWebApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/operator/servers");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        body.Should().NotContain(ReaderWebApplicationFactory.ServerName);
    }

    [Fact]
    public async Task Login_endpoint_is_not_available_when_authentication_is_disabled()
    {
        await using var factory = new DisabledAuthenticationWebApplicationFactory();
        using var client = factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });

        using var response = await client.GetAsync("/auth/login");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Reader_can_view_server_inventory_status_incidents_and_history()
    {
        await using var factory = new ReaderWebApplicationFactory();
        using var client = factory.CreateClient();

        using var listResponse = await client.GetAsync("/operator/servers");
        var listBody = await listResponse.Content.ReadAsStringAsync();
        using var detailResponse = await client.GetAsync(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}");
        var detailBody = await detailResponse.Content.ReadAsStringAsync();
        using var historyResponse = await client.GetAsync(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/history");
        var historyBody = await historyResponse.Content.ReadAsStringAsync();
        using var incidentsResponse = await client.GetAsync("/operator/incidents");
        var incidentsBody = await incidentsResponse.Content.ReadAsStringAsync();

        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        listBody.Should().Contain("Controlled servers");
        listBody.Should().Contain(ReaderWebApplicationFactory.ServerName);
        listBody.Should().Contain("Sign out");
        detailResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        detailBody.Should().Contain("Latest observation");
        detailBody.Should().Contain("The latest A2S probe reached the server.");
        historyResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        historyBody.Should().Contain("Recent observations");
        historyBody.Should().Contain("Probe recovered");
        incidentsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        incidentsBody.Should().Contain("Open incidents");
        incidentsBody.Should().Contain(ReaderWebApplicationFactory.OpenIncidentReason);
    }

    [Fact]
    public async Task Operator_can_view_reader_incidents()
    {
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/operator/incidents");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain(ReaderWebApplicationFactory.OpenIncidentReason);
    }

    [Theory]
    [InlineData("/operator/servers")]
    [InlineData("/operator/incidents")]
    [InlineData("/operator/servers/f130f68c-cb3d-4e18-9dfe-7faf62ce8e3f")]
    [InlineData("/operator/servers/f130f68c-cb3d-4e18-9dfe-7faf62ce8e3f/history")]
    public async Task Signed_in_account_without_role_cannot_view_reader_data(string requestPath)
    {
        await using var factory = new ReaderWebApplicationFactory(role: null);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(requestPath);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("Reader access required");
        body.Should().NotContain(ReaderWebApplicationFactory.ServerName);
    }

    [Fact]
    public async Task Missing_server_history_returns_not_found_state()
    {
        await using var factory = new ReaderWebApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/operator/servers/{Guid.NewGuid():D}/history");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("Server not found");
        body.Should().NotContain(ReaderWebApplicationFactory.ServerName);
    }
}
