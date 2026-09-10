using System.Net;
using AwesomeAssertions;
using GoldSrcOps.Web.Security;
using GoldSrcOps.Web.Services;
using Microsoft.AspNetCore.Mvc.Testing;

namespace GoldSrcOps.WebTests.Pages;

public sealed class OperatorRestartWorkflowIntegrationTests
{
    [Fact]
    public async Task Reader_can_review_restart_readiness_but_not_submit_a_restart()
    {
        await using var factory = new ReaderWebApplicationFactory();
        using var client = factory.CreateClient();

        using var historyResponse = await client.GetAsync(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/commands");
        var historyBody = await historyResponse.Content.ReadAsStringAsync();
        using var formResponse = await client.GetAsync(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/commands/restart");
        var formBody = await formResponse.Content.ReadAsStringAsync();

        historyResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        historyBody.Should().NotContain("Restart server");
        formResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        formBody.Should().Contain("Operator role required");
        formBody.Should().Contain("RCON binding");
        formBody.Should().NotContain("ConfirmationToken");
        formBody.Should().NotContain("restart-form");
    }

    [Fact]
    public async Task Operator_can_open_the_antiforgery_protected_restart_review()
    {
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/commands/restart");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("Restart server");
        body.Should().Contain("Expected impact");
        body.Should().Contain("__RequestVerificationToken");
        body.Should().Contain("ConfirmationToken");
        body.Should().Contain("Queue restart");
        body.Should().NotContain(ReaderWebApplicationFactory.CommandPayloadSentinel);
    }

    [Theory]
    [InlineData(true, true, "Wait for the current command to finish")]
    [InlineData(false, false, "Configure the RCON binding first")]
    public async Task Restart_form_is_locked_when_a_precondition_is_not_met(
        bool commandInProgress,
        bool rconConfigured,
        string expectedMessage)
    {
        await using var factory = new ReaderWebApplicationFactory(
            WebSecurity.OperatorRole,
            commandInProgress: commandInProgress,
            rconConfigured: rconConfigured);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/commands/restart");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain(expectedMessage);
        body.Should().NotContain("ConfirmationToken");
        body.Should().NotContain("restart-form");
    }

    [Fact]
    public async Task Operator_can_queue_one_confirmed_restart()
    {
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        using var client = CreateNonRedirectingClient(factory);
        var form = await LoadRestartFormAsync(client);

        using var response = await client.PostAsync(form.Action, CreateRestartForm(form));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().Be(new Uri(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/commands?result=restart-queued",
            UriKind.Relative));
        factory.OperatorApiClient.RestartCallCount.Should().Be(1);
        factory.OperatorApiClient.LastRestartServerId.Should().Be(
            ReaderWebApplicationFactory.ServerId);
        factory.OperatorApiClient.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Reusing_a_restart_confirmation_does_not_queue_a_second_command()
    {
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        using var client = CreateNonRedirectingClient(factory);
        var form = await LoadRestartFormAsync(client);

        using var firstResponse = await client.PostAsync(form.Action, CreateRestartForm(form));
        using var secondResponse = await client.PostAsync(form.Action, CreateRestartForm(form));

        firstResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        secondResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        secondResponse.Headers.Location.Should().Be(new Uri(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/commands/restart?result=confirmation-expired",
            UriKind.Relative));
        factory.OperatorApiClient.RestartCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Say_confirmation_cannot_be_reused_for_restart()
    {
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        using var client = CreateNonRedirectingClient(factory);
        using var pageResponse = await client.GetAsync(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/commands/new");
        var pageBody = await pageResponse.Content.ReadAsStringAsync();
        var form = new RestartForm(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/commands/restart/queue",
            GetHiddenInputValue(pageBody, "__RequestVerificationToken"),
            GetHiddenInputValue(pageBody, "ConfirmationToken"));

        using var response = await client.PostAsync(form.Action, CreateRestartForm(form));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().Be(new Uri(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/commands/restart?result=confirmation-expired",
            UriKind.Relative));
        factory.OperatorApiClient.RestartCallCount.Should().Be(0);
    }

    [Fact]
    public async Task Submit_rechecks_incomplete_commands_before_the_restart_api_call()
    {
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        using var client = CreateNonRedirectingClient(factory);
        var form = await LoadRestartFormAsync(client);
        factory.ReaderApiClient.CommandInProgress = true;

        using var response = await client.PostAsync(form.Action, CreateRestartForm(form));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().Be(new Uri(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/commands/restart?result=commands-in-progress",
            UriKind.Relative));
        factory.OperatorApiClient.RestartCallCount.Should().Be(0);
    }

    [Fact]
    public async Task Missing_antiforgery_token_is_rejected_before_the_api_call()
    {
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        using var client = CreateNonRedirectingClient(factory);
        var form = await LoadRestartFormAsync(client);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ConfirmationToken"] = form.ConfirmationToken,
            ["Confirmed"] = "true",
        });

        using var response = await client.PostAsync(form.Action, content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        factory.OperatorApiClient.RestartCallCount.Should().Be(0);
    }

    [Fact]
    public async Task Reader_cannot_post_a_restart_with_forged_values()
    {
        await using var factory = new ReaderWebApplicationFactory();
        using var client = CreateNonRedirectingClient(factory);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ConfirmationToken"] = new('a', OperatorCommandConfirmationStore.TokenLength),
            ["Confirmed"] = "true",
        });

        using var response = await client.PostAsync(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/commands/restart/queue",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().Be(new Uri("/auth/forbidden", UriKind.Relative));
        factory.OperatorApiClient.RestartCallCount.Should().Be(0);
    }

    [Fact]
    public async Task Operator_without_subject_cannot_receive_or_submit_a_restart_confirmation()
    {
        await using var factory = new ReaderWebApplicationFactory(
            WebSecurity.OperatorRole,
            subject: null);
        using var client = CreateNonRedirectingClient(factory);

        using var pageResponse = await client.GetAsync(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/commands/restart");
        var pageBody = await pageResponse.Content.ReadAsStringAsync();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ConfirmationToken"] = new('a', OperatorCommandConfirmationStore.TokenLength),
            ["Confirmed"] = "true",
        });
        using var postResponse = await client.PostAsync(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/commands/restart/queue",
            content);

        pageResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        pageBody.Should().Contain("Operator role required");
        pageBody.Should().NotContain("ConfirmationToken");
        postResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        postResponse.Headers.Location.Should().Be(new Uri("/auth/forbidden", UriKind.Relative));
        factory.OperatorApiClient.RestartCallCount.Should().Be(0);
    }

    [Theory]
    [InlineData((int)OperatorCommandQueueResult.ServerNotFound, "server-not-found")]
    [InlineData((int)OperatorCommandQueueResult.MissingRconCredential, "credential-missing")]
    [InlineData((int)OperatorCommandQueueResult.Rejected, "rejected")]
    public async Task Api_rejection_returns_to_restart_review_without_retrying(
        int resultKind,
        string result)
    {
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        factory.OperatorApiClient.RestartResult = (OperatorCommandQueueResult)resultKind;
        using var client = CreateNonRedirectingClient(factory);
        var form = await LoadRestartFormAsync(client);

        using var response = await client.PostAsync(form.Action, CreateRestartForm(form));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().Be(new Uri(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/commands/restart?result={result}",
            UriKind.Relative));
        factory.OperatorApiClient.RestartCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Unknown_restart_outcome_is_not_retried_and_redirects_to_history()
    {
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        factory.OperatorApiClient.RestartExceptionToThrow =
            new HttpRequestException("Simulated transport failure");
        using var client = CreateNonRedirectingClient(factory);
        var form = await LoadRestartFormAsync(client);

        using var response = await client.PostAsync(form.Action, CreateRestartForm(form));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().Be(new Uri(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/commands?result=restart-unknown",
            UriKind.Relative));
        factory.OperatorApiClient.RestartCallCount.Should().Be(1);
    }

    private static HttpClient CreateNonRedirectingClient(ReaderWebApplicationFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

    private static async Task<RestartForm> LoadRestartFormAsync(HttpClient client)
    {
        var page = $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/commands/restart";
        var action = $"{page}/queue";
        using var response = await client.GetAsync(page);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain($"action=\"{action}\"");
        return new RestartForm(
            action,
            GetHiddenInputValue(body, "__RequestVerificationToken"),
            GetHiddenInputValue(body, "ConfirmationToken"));
    }

    private static FormUrlEncodedContent CreateRestartForm(RestartForm form) =>
        new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["__RequestVerificationToken"] = form.AntiforgeryToken,
            ["ConfirmationToken"] = form.ConfirmationToken,
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

    private sealed record RestartForm(
        string Action,
        string AntiforgeryToken,
        string ConfirmationToken);
}
