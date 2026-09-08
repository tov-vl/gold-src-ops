using System.Net;
using AwesomeAssertions;
using GoldSrcOps.Web.Security;
using Microsoft.AspNetCore.Mvc.Testing;

namespace GoldSrcOps.WebTests.Pages;

public sealed class OperatorReplayWorkflowIntegrationTests
{
    [Fact]
    public async Task Reader_can_view_dead_letter_but_not_the_replay_form()
    {
        await using var factory = new ReaderWebApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/operator/dead-letters/{ReaderWebApplicationFactory.DeadLetterEventId:D}");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain(ReaderWebApplicationFactory.DeadLetterLastError);
        body.Should().Contain("Operator role required");
        body.Should().NotContain("ConfirmationToken");
        body.Should().NotContain("Queue replay");
    }

    [Fact]
    public async Task Operator_can_open_the_antiforgery_protected_replay_form()
    {
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/operator/dead-letters/{ReaderWebApplicationFactory.DeadLetterEventId:D}");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("Replay delivery");
        body.Should().Contain("__RequestVerificationToken");
        body.Should().Contain("ConfirmationToken");
        body.Should().Contain("RequestId");
        body.Should().Contain("Queue replay");
        body.Should().NotContain(ReaderWebApplicationFactory.DeadLetterPayloadSentinel);
    }

    [Fact]
    public async Task Operator_can_queue_one_replay_and_reason_is_not_reflected()
    {
        const string reason = "Receiver recovery verified by the operator";
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        using var client = CreateNonRedirectingClient(factory);
        var form = await LoadReplayFormAsync(client);

        using var response = await client.PostAsync(
            form.Action,
            CreateReplayForm(form, $"  {reason}  "));
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().Be(new Uri(
            $"/operator/replays/{form.RequestId:D}?result=accepted",
            UriKind.Relative));
        body.Should().NotContain(reason);
        response.Headers.Location!.OriginalString.Should().NotContain(reason);
        factory.OperatorApiClient.ReplayCallCount.Should().Be(1);
        factory.OperatorApiClient.LastEventId.Should().Be(ReaderWebApplicationFactory.DeadLetterEventId);
        factory.OperatorApiClient.LastRequestId.Should().Be(form.RequestId);
        factory.OperatorApiClient.LastReason.Should().Be(reason);
    }

    [Fact]
    public async Task Reusing_a_confirmation_does_not_queue_a_second_replay()
    {
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        using var client = CreateNonRedirectingClient(factory);
        var form = await LoadReplayFormAsync(client);

        using var firstResponse = await client.PostAsync(
            form.Action,
            CreateReplayForm(form, "First replay"));
        using var secondResponse = await client.PostAsync(
            form.Action,
            CreateReplayForm(form, "Duplicate replay"));

        firstResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        secondResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        secondResponse.Headers.Location.Should().Be(new Uri(
            $"/operator/dead-letters/{ReaderWebApplicationFactory.DeadLetterEventId:D}?result=confirmation-expired",
            UriKind.Relative));
        factory.OperatorApiClient.ReplayCallCount.Should().Be(1);
        factory.OperatorApiClient.LastReason.Should().Be("First replay");
    }

    [Fact]
    public async Task Missing_antiforgery_token_is_rejected_before_the_replay_api_call()
    {
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        using var client = CreateNonRedirectingClient(factory);
        var form = await LoadReplayFormAsync(client);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ConfirmationToken"] = form.ConfirmationToken,
            ["RequestId"] = form.RequestId.ToString("D"),
            ["Reason"] = "Missing antiforgery token",
            ["Confirmed"] = "true",
        });

        using var response = await client.PostAsync(form.Action, content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        factory.OperatorApiClient.ReplayCallCount.Should().Be(0);
    }

    [Fact]
    public async Task Reader_cannot_post_a_replay_even_with_forged_form_values()
    {
        await using var factory = new ReaderWebApplicationFactory();
        using var client = CreateNonRedirectingClient(factory);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ConfirmationToken"] = new('a', 43),
            ["RequestId"] = Guid.NewGuid().ToString("D"),
            ["Reason"] = "Forged replay",
            ["Confirmed"] = "true",
        });

        using var response = await client.PostAsync(
            $"/operator/dead-letters/{ReaderWebApplicationFactory.DeadLetterEventId:D}/replay",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().Be(new Uri("/auth/forbidden", UriKind.Relative));
        factory.OperatorApiClient.ReplayCallCount.Should().Be(0);
    }

    [Fact]
    public async Task Unknown_api_outcome_is_not_retried_and_redirects_to_the_receipt()
    {
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        factory.OperatorApiClient.ReplayExceptionToThrow =
            new HttpRequestException("Simulated transport failure");
        using var client = CreateNonRedirectingClient(factory);
        var form = await LoadReplayFormAsync(client);

        using var response = await client.PostAsync(
            form.Action,
            CreateReplayForm(form, "Uncertain replay"));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().Be(new Uri(
            $"/operator/replays/{form.RequestId:D}?result=unknown",
            UriKind.Relative));
        factory.OperatorApiClient.ReplayCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Reader_can_view_a_durable_replay_receipt_without_raw_payload()
    {
        await using var factory = new ReaderWebApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/operator/replays/{ReaderWebApplicationFactory.ReplayRequestId:D}");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("Replay #2");
        body.Should().Contain(ReaderWebApplicationFactory.ReplayReason);
        body.Should().Contain(ReaderWebApplicationFactory.ReplayRequestId.ToString("D"));
        body.Should().NotContain(ReaderWebApplicationFactory.DeadLetterPayloadSentinel);
    }

    private static HttpClient CreateNonRedirectingClient(ReaderWebApplicationFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

    private static async Task<ReplayForm> LoadReplayFormAsync(HttpClient client)
    {
        var action = $"/operator/dead-letters/{ReaderWebApplicationFactory.DeadLetterEventId:D}/replay";
        using var response = await client.GetAsync(
            $"/operator/dead-letters/{ReaderWebApplicationFactory.DeadLetterEventId:D}");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return new ReplayForm(
            action,
            GetHiddenInputValue(body, "__RequestVerificationToken"),
            GetHiddenInputValue(body, "ConfirmationToken"),
            Guid.ParseExact(GetHiddenInputValue(body, "RequestId"), "D"));
    }

    private static FormUrlEncodedContent CreateReplayForm(ReplayForm form, string reason) =>
        new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["__RequestVerificationToken"] = form.AntiforgeryToken,
            ["ConfirmationToken"] = form.ConfirmationToken,
            ["RequestId"] = form.RequestId.ToString("D"),
            ["Reason"] = reason,
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

    private sealed record ReplayForm(
        string Action,
        string AntiforgeryToken,
        string ConfirmationToken,
        Guid RequestId);
}
