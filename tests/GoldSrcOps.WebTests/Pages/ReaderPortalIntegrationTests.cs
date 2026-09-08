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
    public async Task Reader_can_view_command_history_and_dead_letters_without_raw_payloads()
    {
        await using var factory = new ReaderWebApplicationFactory();
        using var client = factory.CreateClient();

        using var commandsResponse = await client.GetAsync(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/commands");
        var commandsBody = await commandsResponse.Content.ReadAsStringAsync();
        using var deadLettersResponse = await client.GetAsync("/operator/dead-letters");
        var deadLettersBody = await deadLettersResponse.Content.ReadAsStringAsync();
        using var deadLetterDetailResponse = await client.GetAsync(
            $"/operator/dead-letters/{ReaderWebApplicationFactory.DeadLetterEventId:D}");
        var deadLetterDetailBody = await deadLetterDetailResponse.Content.ReadAsStringAsync();
        using var replayResponse = await client.GetAsync(
            $"/operator/replays/{ReaderWebApplicationFactory.ReplayRequestId:D}");
        var replayBody = await replayResponse.Content.ReadAsStringAsync();

        commandsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        commandsBody.Should().Contain("Command history");
        commandsBody.Should().Contain(ReaderWebApplicationFactory.CommandResultSummary);
        commandsBody.Should().NotContain(ReaderWebApplicationFactory.CommandPayloadSentinel);
        deadLettersResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        deadLettersBody.Should().Contain("Current dead letters");
        deadLettersBody.Should().Contain("v1");
        deadLettersBody.Should().Contain(ReaderWebApplicationFactory.DeadLetterLastError);
        deadLetterDetailResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        deadLetterDetailBody.Should().Contain("Ordering warning");
        deadLetterDetailBody.Should().Contain(ReaderWebApplicationFactory.DeadLetterLastError);
        deadLetterDetailBody.Should().NotContain(ReaderWebApplicationFactory.DeadLetterPayloadSentinel);
        replayResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        replayBody.Should().Contain(ReaderWebApplicationFactory.ReplayReason);
        replayBody.Should().NotContain(ReaderWebApplicationFactory.DeadLetterPayloadSentinel);
    }

    [Fact]
    public async Task Operator_can_view_reader_operations_data()
    {
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        using var client = factory.CreateClient();

        using var incidentsResponse = await client.GetAsync("/operator/incidents");
        var incidentsBody = await incidentsResponse.Content.ReadAsStringAsync();
        using var commandsResponse = await client.GetAsync(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/commands");
        using var deadLettersResponse = await client.GetAsync("/operator/dead-letters");

        incidentsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        incidentsBody.Should().Contain(ReaderWebApplicationFactory.OpenIncidentReason);
        commandsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        deadLettersResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("/operator/servers")]
    [InlineData("/operator/incidents")]
    [InlineData("/operator/servers/f130f68c-cb3d-4e18-9dfe-7faf62ce8e3f")]
    [InlineData("/operator/servers/f130f68c-cb3d-4e18-9dfe-7faf62ce8e3f/history")]
    [InlineData("/operator/servers/f130f68c-cb3d-4e18-9dfe-7faf62ce8e3f/commands")]
    [InlineData("/operator/servers/f130f68c-cb3d-4e18-9dfe-7faf62ce8e3f/commands/new")]
    [InlineData("/operator/dead-letters")]
    [InlineData("/operator/dead-letters/70d51faf-6029-4b1e-a922-b7a3ab8d1f84")]
    [InlineData("/operator/replays/4fb7401c-802c-48b9-aa71-5e27619b0784")]
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

    [Fact]
    public async Task Missing_command_history_returns_not_found_state()
    {
        await using var factory = new ReaderWebApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/operator/servers/{Guid.NewGuid():D}/commands");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("Server not found");
        body.Should().NotContain(ReaderWebApplicationFactory.ServerName);
    }

    [Fact]
    public async Task Missing_dead_letter_returns_not_found_state()
    {
        await using var factory = new ReaderWebApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/operator/dead-letters/{Guid.NewGuid():D}");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("Dead letter not found");
        body.Should().NotContain(ReaderWebApplicationFactory.DeadLetterLastError);
    }

}
