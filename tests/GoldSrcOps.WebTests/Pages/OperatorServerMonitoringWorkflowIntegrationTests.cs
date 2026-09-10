using System.Net;
using AwesomeAssertions;
using GoldSrcOps.Web.Security;
using GoldSrcOps.Web.Services;
using Microsoft.AspNetCore.Mvc.Testing;

namespace GoldSrcOps.WebTests.Pages;

public sealed class OperatorServerMonitoringWorkflowIntegrationTests
{
    [Fact]
    public async Task Reader_can_view_monitoring_state_but_not_lifecycle_controls()
    {
        await using var factory = new ReaderWebApplicationFactory();
        using var client = factory.CreateClient();

        using var statusResponse = await client.GetAsync(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}");
        var statusBody = await statusResponse.Content.ReadAsStringAsync();
        using var formResponse = await client.GetAsync(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/monitoring");
        var formBody = await formResponse.Content.ReadAsStringAsync();

        statusResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        statusBody.Should().Contain("Every 30 seconds");
        statusBody.Should().NotContain("Manage monitoring");
        formResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        formBody.Should().Contain("Operator role required");
        formBody.Should().NotContain("ConfirmationToken");
        formBody.Should().NotContain("monitoring-form");
    }

    [Theory]
    [InlineData(true, "disable", "Pause monitoring")]
    [InlineData(false, "enable", "Resume monitoring")]
    public async Task Operator_receives_the_action_for_the_current_monitoring_state(
        bool serverEnabled,
        string actionSegment,
        string actionLabel)
    {
        await using var factory = new ReaderWebApplicationFactory(
            WebSecurity.OperatorRole,
            serverEnabled: serverEnabled);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/monitoring");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain(actionLabel);
        body.Should().Contain(
            $"action=\"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/monitoring/{actionSegment}\"");
        body.Should().Contain("__RequestVerificationToken");
        body.Should().Contain("ConfirmationToken");
    }

    [Theory]
    [InlineData(true, false, "disable", "monitoring-disabled")]
    [InlineData(false, true, "enable", "monitoring-enabled")]
    public async Task Operator_can_submit_one_confirmed_monitoring_change(
        bool serverEnabled,
        bool requestedEnabled,
        string actionSegment,
        string result)
    {
        await using var factory = new ReaderWebApplicationFactory(
            WebSecurity.OperatorRole,
            serverEnabled: serverEnabled);
        using var client = CreateNonRedirectingClient(factory);
        var form = await LoadMonitoringFormAsync(client, actionSegment);

        using var response = await client.PostAsync(form.Action, CreateMonitoringForm(form));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().Be(new Uri(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}?result={result}",
            UriKind.Relative));
        factory.OperatorApiClient.MonitoringCallCount.Should().Be(1);
        factory.OperatorApiClient.LastMonitoringServerId.Should().Be(
            ReaderWebApplicationFactory.ServerId);
        factory.OperatorApiClient.LastMonitoringEnabled.Should().Be(requestedEnabled);
    }

    [Theory]
    [InlineData((int)OperatorMonitoringUpdateResult.ServerNotFound, "server-not-found")]
    [InlineData((int)OperatorMonitoringUpdateResult.Conflict, "monitoring-conflict")]
    public async Task Api_rejection_returns_to_the_form_without_retrying(
        int resultKind,
        string result)
    {
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        factory.OperatorApiClient.MonitoringResult =
            (OperatorMonitoringUpdateResult)resultKind;
        using var client = CreateNonRedirectingClient(factory);
        var form = await LoadMonitoringFormAsync(client, "disable");

        using var response = await client.PostAsync(form.Action, CreateMonitoringForm(form));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().Be(new Uri(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/monitoring?result={result}",
            UriKind.Relative));
        factory.OperatorApiClient.MonitoringCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Confirmation_is_bound_to_the_action_and_can_be_used_only_once()
    {
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        using var client = CreateNonRedirectingClient(factory);
        var form = await LoadMonitoringFormAsync(client, "disable");
        var wrongAction = $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/monitoring/enable";

        using var wrongActionResponse = await client.PostAsync(
            wrongAction,
            CreateMonitoringForm(form));
        using var acceptedResponse = await client.PostAsync(
            form.Action,
            CreateMonitoringForm(form));
        using var repeatedResponse = await client.PostAsync(
            form.Action,
            CreateMonitoringForm(form));

        wrongActionResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        wrongActionResponse.Headers.Location.Should().Be(new Uri(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/monitoring?result=confirmation-expired",
            UriKind.Relative));
        acceptedResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        repeatedResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        repeatedResponse.Headers.Location.Should().Be(new Uri(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/monitoring?result=confirmation-expired",
            UriKind.Relative));
        factory.OperatorApiClient.MonitoringCallCount.Should().Be(1);
        factory.OperatorApiClient.LastMonitoringEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Missing_antiforgery_token_is_rejected_before_the_api_call()
    {
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        using var client = CreateNonRedirectingClient(factory);
        var form = await LoadMonitoringFormAsync(client, "disable");
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ConfirmationToken"] = form.ConfirmationToken,
            ["Confirmed"] = "true",
        });

        using var response = await client.PostAsync(form.Action, content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        factory.OperatorApiClient.MonitoringCallCount.Should().Be(0);
    }

    [Fact]
    public async Task Reader_cannot_post_a_monitoring_change_with_forged_values()
    {
        await using var factory = new ReaderWebApplicationFactory();
        using var client = CreateNonRedirectingClient(factory);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ConfirmationToken"] = new('a', 43),
            ["Confirmed"] = "true",
        });

        using var response = await client.PostAsync(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/monitoring/disable",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().Be(new Uri("/auth/forbidden", UriKind.Relative));
        factory.OperatorApiClient.MonitoringCallCount.Should().Be(0);
    }

    [Fact]
    public async Task Operator_without_subject_cannot_receive_or_submit_a_confirmation()
    {
        await using var factory = new ReaderWebApplicationFactory(
            WebSecurity.OperatorRole,
            subject: null);
        using var client = CreateNonRedirectingClient(factory);

        using var pageResponse = await client.GetAsync(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/monitoring");
        var pageBody = await pageResponse.Content.ReadAsStringAsync();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ConfirmationToken"] = new('a', 43),
            ["Confirmed"] = "true",
        });
        using var postResponse = await client.PostAsync(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/monitoring/disable",
            content);

        pageResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        pageBody.Should().Contain("Operator role required");
        pageBody.Should().NotContain("ConfirmationToken");
        postResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        postResponse.Headers.Location.Should().Be(new Uri("/auth/forbidden", UriKind.Relative));
        factory.OperatorApiClient.MonitoringCallCount.Should().Be(0);
    }

    [Fact]
    public async Task Unknown_api_outcome_is_not_retried_and_redirects_to_fresh_status()
    {
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        factory.OperatorApiClient.MonitoringExceptionToThrow =
            new HttpRequestException("Simulated transport failure");
        using var client = CreateNonRedirectingClient(factory);
        var form = await LoadMonitoringFormAsync(client, "disable");

        using var response = await client.PostAsync(form.Action, CreateMonitoringForm(form));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().Be(new Uri(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}?result=monitoring-unknown",
            UriKind.Relative));
        factory.OperatorApiClient.MonitoringCallCount.Should().Be(1);
    }

    private static HttpClient CreateNonRedirectingClient(ReaderWebApplicationFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

    private static async Task<MonitoringForm> LoadMonitoringFormAsync(
        HttpClient client,
        string actionSegment)
    {
        var action =
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/monitoring/{actionSegment}";
        using var response = await client.GetAsync(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/monitoring");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain($"action=\"{action}\"");
        return new MonitoringForm(
            action,
            GetHiddenInputValue(body, "__RequestVerificationToken"),
            GetHiddenInputValue(body, "ConfirmationToken"));
    }

    private static FormUrlEncodedContent CreateMonitoringForm(MonitoringForm form) =>
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

    private sealed record MonitoringForm(
        string Action,
        string AntiforgeryToken,
        string ConfirmationToken);
}
