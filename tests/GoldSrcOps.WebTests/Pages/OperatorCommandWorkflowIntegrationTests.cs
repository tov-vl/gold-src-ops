using System.Net;
using AwesomeAssertions;
using GoldSrcOps.Web.Security;
using Microsoft.AspNetCore.Mvc.Testing;

namespace GoldSrcOps.WebTests.Pages;

public sealed class OperatorCommandWorkflowIntegrationTests
{
    [Fact]
    public async Task Reader_can_view_command_history_but_not_the_command_form()
    {
        await using var factory = new ReaderWebApplicationFactory();
        using var client = factory.CreateClient();

        using var historyResponse = await client.GetAsync(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/commands");
        var historyBody = await historyResponse.Content.ReadAsStringAsync();
        using var formResponse = await client.GetAsync(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/commands/new");
        var formBody = await formResponse.Content.ReadAsStringAsync();

        historyResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        historyBody.Should().NotContain("Broadcast message");
        formResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        formBody.Should().Contain("Operator role required");
        formBody.Should().NotContain("ConfirmationToken");
        formBody.Should().NotContain("Queue message");
    }

    [Fact]
    public async Task Operator_can_open_the_antiforgery_protected_say_form()
    {
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/commands/new");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("Broadcast message");
        body.Should().Contain("__RequestVerificationToken");
        body.Should().Contain("ConfirmationToken");
        body.Should().Contain("Queue message");
        body.Should().NotContain(ReaderWebApplicationFactory.CommandPayloadSentinel);
    }

    [Fact]
    public async Task Operator_can_queue_one_say_command_and_message_is_not_reflected()
    {
        const string message = "UI message payload must not be reflected";
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        using var client = CreateNonRedirectingClient(factory);
        var form = await LoadCommandFormAsync(client);

        using var response = await client.PostAsync(
            form.Action,
            CreateCommandForm(form, message));
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().Be(new Uri(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/commands?result=queued",
            UriKind.Relative));
        body.Should().NotContain(message);
        response.Headers.Location!.OriginalString.Should().NotContain(message);
        factory.OperatorApiClient.CallCount.Should().Be(1);
        factory.OperatorApiClient.LastServerId.Should().Be(ReaderWebApplicationFactory.ServerId);
        factory.OperatorApiClient.LastMessage.Should().Be(message);
    }

    [Fact]
    public async Task Reusing_a_confirmation_does_not_queue_a_second_command()
    {
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        using var client = CreateNonRedirectingClient(factory);
        var form = await LoadCommandFormAsync(client);

        using var firstResponse = await client.PostAsync(
            form.Action,
            CreateCommandForm(form, "First submission"));
        using var secondResponse = await client.PostAsync(
            form.Action,
            CreateCommandForm(form, "Duplicate submission"));

        firstResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        secondResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        secondResponse.Headers.Location.Should().Be(new Uri(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/commands/new?result=confirmation-expired",
            UriKind.Relative));
        factory.OperatorApiClient.CallCount.Should().Be(1);
        factory.OperatorApiClient.LastMessage.Should().Be("First submission");
    }

    [Fact]
    public async Task Missing_antiforgery_token_is_rejected_before_the_api_call()
    {
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        using var client = CreateNonRedirectingClient(factory);
        var form = await LoadCommandFormAsync(client);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ConfirmationToken"] = form.ConfirmationToken,
            ["Message"] = "Missing antiforgery token",
            ["Confirmed"] = "true",
        });

        using var response = await client.PostAsync(form.Action, content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        factory.OperatorApiClient.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Reader_cannot_post_a_command_even_with_forged_form_values()
    {
        await using var factory = new ReaderWebApplicationFactory();
        using var client = CreateNonRedirectingClient(factory);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ConfirmationToken"] = new('a', 43),
            ["Message"] = "Forged message",
            ["Confirmed"] = "true",
        });

        using var response = await client.PostAsync(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/commands/say",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().Be(new Uri("/auth/forbidden", UriKind.Relative));
        factory.OperatorApiClient.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Operator_without_subject_cannot_receive_or_submit_a_command_confirmation()
    {
        await using var factory = new ReaderWebApplicationFactory(
            WebSecurity.OperatorRole,
            subject: null);
        using var client = CreateNonRedirectingClient(factory);

        using var pageResponse = await client.GetAsync(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/commands/new");
        var pageBody = await pageResponse.Content.ReadAsStringAsync();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ConfirmationToken"] = new('a', 43),
            ["Message"] = "Forged message",
            ["Confirmed"] = "true",
        });
        using var postResponse = await client.PostAsync(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/commands/say",
            content);

        pageResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        pageBody.Should().Contain("Operator role required");
        pageBody.Should().NotContain("ConfirmationToken");
        postResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        postResponse.Headers.Location.Should().Be(new Uri("/auth/forbidden", UriKind.Relative));
        factory.OperatorApiClient.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Unknown_api_outcome_is_not_retried_and_redirects_to_history()
    {
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        factory.OperatorApiClient.ExceptionToThrow = new HttpRequestException("Simulated transport failure");
        using var client = CreateNonRedirectingClient(factory);
        var form = await LoadCommandFormAsync(client);

        using var response = await client.PostAsync(
            form.Action,
            CreateCommandForm(form, "Uncertain submission"));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().Be(new Uri(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/commands?result=unknown",
            UriKind.Relative));
        factory.OperatorApiClient.CallCount.Should().Be(1);
    }

    private static HttpClient CreateNonRedirectingClient(ReaderWebApplicationFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

    private static async Task<CommandForm> LoadCommandFormAsync(HttpClient client)
    {
        var action = $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/commands/say";
        using var response = await client.GetAsync(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/commands/new");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return new CommandForm(
            action,
            GetHiddenInputValue(body, "__RequestVerificationToken"),
            GetHiddenInputValue(body, "ConfirmationToken"));
    }

    private static FormUrlEncodedContent CreateCommandForm(CommandForm form, string message) =>
        new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["__RequestVerificationToken"] = form.AntiforgeryToken,
            ["ConfirmationToken"] = form.ConfirmationToken,
            ["Message"] = message,
            ["Confirmed"] = "true",
        });

    private static string GetHiddenInputValue(string html, string name)
    {
        var nameMarker = $"name=\"{name}\"";
        var nameIndex = html.IndexOf(nameMarker, StringComparison.Ordinal);
        nameIndex.Should().BeGreaterThanOrEqualTo(0);
        var tagStart = html.LastIndexOf('<', nameIndex);
        var tagEnd = html.IndexOf('>', nameIndex);
        tagStart.Should().BeGreaterThanOrEqualTo(0);
        tagEnd.Should().BeGreaterThan(tagStart);
        var input = html[tagStart..tagEnd];
        const string valueMarker = "value=\"";
        var valueStart = input.IndexOf(valueMarker, StringComparison.Ordinal);
        valueStart.Should().BeGreaterThanOrEqualTo(0);
        valueStart += valueMarker.Length;
        var valueEnd = input.IndexOf('"', valueStart);
        valueEnd.Should().BeGreaterThan(valueStart);
        return WebUtility.HtmlDecode(input[valueStart..valueEnd]);
    }

    private sealed record CommandForm(
        string Action,
        string AntiforgeryToken,
        string ConfirmationToken);
}
