using System.Net;
using AwesomeAssertions;
using GoldSrcOps.Contracts.Alerts;
using GoldSrcOps.Contracts.ProviderDelivery;
using GoldSrcOps.Web.Security;
using GoldSrcOps.Web.Services;
using Microsoft.AspNetCore.Mvc.Testing;

namespace GoldSrcOps.WebTests.Pages;

public sealed class OperatorProviderReviewWorkflowIntegrationTests
{
    [Theory]
    [InlineData("/operator/provider-delivery")]
    [InlineData("/operator/provider-delivery/dead-letters")]
    public async Task Reader_cannot_open_provider_operations_pages(string path)
    {
        await using var factory = new ReaderWebApplicationFactory();
        using var client = CreateNonRedirectingClient(factory);

        using var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().Be(new Uri("/auth/forbidden", UriKind.Relative));
    }

    [Fact]
    public async Task Operator_can_view_provider_delivery_overview_without_mutation_controls()
    {
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/operator/provider-delivery");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("Some provider messages remain terminal");
        body.Should().Contain("End-to-end readiness");
        body.Should().Contain("Control-plane sender");
        body.Should().Contain("Receiver mode");
        body.Should().Contain("CatchUp");
        body.Should().Contain("Action required");
        body.Should().Contain("Delivery worker");
        body.Should().Contain("Disabled");
        body.Should().Contain("Awaiting review");
        body.Should().Contain("Review provider dead letters");
        body.Should().Contain("cannot retry, replay, delete, reclassify, unblock, or enable");
        body.Should().NotContain("Authorization");
        body.Should().NotContain("Record review");
    }

    [Fact]
    public async Task Delivery_chain_distinguishes_safely_paused_state()
    {
        var observedAtUtc = new DateTimeOffset(2026, 9, 21, 14, 0, 0, TimeSpan.Zero);
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        factory.ReaderApiClient.AlertDeliveryStatus = new AlertDeliveryStatusResponse(
            false,
            0,
            0,
            0,
            null,
            observedAtUtc);
        factory.ProviderOperationsClient.Status = new ProviderDeliveryStatusResponse(
            false,
            "CatchUp",
            0,
            0,
            0,
            0,
            null,
            observedAtUtc);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/operator/provider-delivery");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("Safely paused");
        body.Should().Contain("The complete delivery chain is intentionally paused");
    }

    [Fact]
    public async Task Delivery_chain_distinguishes_live_state()
    {
        var observedAtUtc = new DateTimeOffset(2026, 9, 21, 14, 0, 0, TimeSpan.Zero);
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        factory.ReaderApiClient.AlertDeliveryStatus = new AlertDeliveryStatusResponse(
            true,
            0,
            0,
            0,
            null,
            observedAtUtc);
        factory.ProviderOperationsClient.Status = new ProviderDeliveryStatusResponse(
            true,
            "Live",
            0,
            0,
            0,
            0,
            null,
            observedAtUtc);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/operator/provider-delivery");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("The complete delivery chain is active");
        body.Should().Contain("New source events can create provider work");
    }

    [Fact]
    public async Task Delivery_chain_fails_closed_when_receiver_mode_is_unavailable()
    {
        var observedAtUtc = new DateTimeOffset(2026, 9, 21, 14, 0, 0, TimeSpan.Zero);
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        factory.ReaderApiClient.AlertDeliveryStatus = new AlertDeliveryStatusResponse(
            false,
            0,
            0,
            0,
            null,
            observedAtUtc);
        factory.ProviderOperationsClient.Status = new ProviderDeliveryStatusResponse(
            false,
            string.Empty,
            0,
            0,
            0,
            0,
            null,
            observedAtUtc);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/operator/provider-delivery");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("Unknown");
        body.Should().Contain("Partially active");
        body.Should().NotContain("Safely paused");
    }

    [Fact]
    public async Task Operator_can_inspect_sanitized_provider_dead_letter_and_review_form()
    {
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        using var client = factory.CreateClient();

        using var listResponse = await client.GetAsync("/operator/provider-delivery/dead-letters");
        using var detailResponse = await client.GetAsync(
            $"/operator/provider-delivery/dead-letters/{ReaderWebApplicationFactory.ProviderMessageId:D}");
        var listBody = await listResponse.Content.ReadAsStringAsync();
        var detailBody = await detailResponse.Content.ReadAsStringAsync();

        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        detailResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        listBody.Should().Contain(ReaderWebApplicationFactory.ProviderFailureSummary);
        detailBody.Should().Contain("Record review");
        detailBody.Should().Contain("__RequestVerificationToken");
        detailBody.Should().Contain("does not retry, replay, delete, reclassify, or unblock");
        detailBody.Should().NotContain("Authorization");
    }

    [Fact]
    public async Task Operator_records_one_review_with_authenticated_subject_and_trimmed_reason()
    {
        const string reason = "Reviewed provider response and incident state";
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        using var client = CreateNonRedirectingClient(factory);
        var form = await LoadReviewFormAsync(client);

        using var response = await client.PostAsync(form.Action, CreateReviewForm(form, $"  {reason}  "));
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().Be(new Uri(
            $"/operator/provider-delivery/reviews/{form.RequestId:D}?result=accepted",
            UriKind.Relative));
        factory.ProviderOperationsClient.ReviewCallCount.Should().Be(1);
        factory.ProviderOperationsClient.LastMessageId.Should().Be(ReaderWebApplicationFactory.ProviderMessageId);
        factory.ProviderOperationsClient.LastRequestId.Should().Be(form.RequestId);
        factory.ProviderOperationsClient.LastRequestedBy.Should().Be(ReaderWebApplicationFactory.Subject);
        factory.ProviderOperationsClient.LastReason.Should().Be(reason);
        body.Should().NotContain(reason);
        response.Headers.Location!.OriginalString.Should().NotContain(reason);
    }

    [Fact]
    public async Task Confirmation_is_single_use()
    {
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        using var client = CreateNonRedirectingClient(factory);
        var form = await LoadReviewFormAsync(client);

        using var first = await client.PostAsync(form.Action, CreateReviewForm(form, "First review"));
        using var second = await client.PostAsync(form.Action, CreateReviewForm(form, "Duplicate review"));

        first.StatusCode.Should().Be(HttpStatusCode.Redirect);
        second.Headers.Location.Should().Be(new Uri(
            $"/operator/provider-delivery/dead-letters/{ReaderWebApplicationFactory.ProviderMessageId:D}?result=confirmation-expired",
            UriKind.Relative));
        factory.ProviderOperationsClient.ReviewCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Missing_antiforgery_token_is_rejected_before_receiver_call()
    {
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        using var client = CreateNonRedirectingClient(factory);
        var form = await LoadReviewFormAsync(client);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ConfirmationToken"] = form.ConfirmationToken,
            ["RequestId"] = form.RequestId.ToString("D"),
            ["Reason"] = "Missing antiforgery token",
            ["Confirmed"] = "true",
        });

        using var response = await client.PostAsync(form.Action, content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        factory.ProviderOperationsClient.ReviewCallCount.Should().Be(0);
    }

    [Fact]
    public async Task Unknown_receiver_outcome_is_not_retried_and_redirects_to_receipt()
    {
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        factory.ProviderOperationsClient.ReviewExceptionToThrow =
            new HttpRequestException("Simulated transport failure");
        using var client = CreateNonRedirectingClient(factory);
        var form = await LoadReviewFormAsync(client);

        using var response = await client.PostAsync(form.Action, CreateReviewForm(form, "Uncertain review"));

        response.Headers.Location.Should().Be(new Uri(
            $"/operator/provider-delivery/reviews/{form.RequestId:D}?result=unknown",
            UriKind.Relative));
        factory.ProviderOperationsClient.ReviewCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Existing_review_is_rendered_without_a_second_review_form()
    {
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        factory.ProviderOperationsClient.IncludeExistingReview = true;
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/operator/provider-delivery/dead-letters/{ReaderWebApplicationFactory.ProviderMessageId:D}");
        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("Immutable review");
        body.Should().Contain(ReaderWebApplicationFactory.ProviderReviewReason);
        body.Should().NotContain("ConfirmationToken");
        body.Should().NotContain("Record review");
    }

    [Fact]
    public async Task Operator_can_view_durable_review_receipt()
    {
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/operator/provider-delivery/reviews/{ReaderWebApplicationFactory.ProviderReviewRequestId:D}");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("Review recorded");
        body.Should().Contain(ReaderWebApplicationFactory.ProviderReviewReason);
        body.Should().Contain("source message remains dead-lettered");
    }

    private static HttpClient CreateNonRedirectingClient(ReaderWebApplicationFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task<ReviewForm> LoadReviewFormAsync(HttpClient client)
    {
        var action = $"/operator/provider-delivery/dead-letters/{ReaderWebApplicationFactory.ProviderMessageId:D}/review";
        using var response = await client.GetAsync(
            $"/operator/provider-delivery/dead-letters/{ReaderWebApplicationFactory.ProviderMessageId:D}");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return new ReviewForm(
            action,
            GetHiddenInputValue(body, "__RequestVerificationToken"),
            GetHiddenInputValue(body, "ConfirmationToken"),
            Guid.ParseExact(GetHiddenInputValue(body, "RequestId"), "D"));
    }

    private static FormUrlEncodedContent CreateReviewForm(ReviewForm form, string reason) =>
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
        var nameIndex = html.IndexOf($"name=\"{name}\"", StringComparison.Ordinal);
        nameIndex.Should().BeGreaterThanOrEqualTo(0);
        var tagStart = html.LastIndexOf('<', nameIndex);
        var tagEnd = html.IndexOf('>', nameIndex);
        var input = html[tagStart..tagEnd];
        const string marker = "value=\"";
        var valueStart = input.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var valueEnd = input.IndexOf('"', valueStart);
        return WebUtility.HtmlDecode(input[valueStart..valueEnd]);
    }

    private sealed record ReviewForm(
        string Action,
        string AntiforgeryToken,
        string ConfirmationToken,
        Guid RequestId);
}
