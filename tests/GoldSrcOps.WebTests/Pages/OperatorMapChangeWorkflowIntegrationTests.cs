using System.Net;
using AwesomeAssertions;
using GoldSrcOps.Web.Security;
using GoldSrcOps.Web.Services;
using Microsoft.AspNetCore.Mvc.Testing;

namespace GoldSrcOps.WebTests.Pages;

public sealed class OperatorMapChangeWorkflowIntegrationTests
{
    [Fact]
    public async Task Reader_can_review_readiness_but_not_prepare_a_map_change()
    {
        await using var factory = new ReaderWebApplicationFactory();
        using var client = factory.CreateClient();

        using var historyResponse = await client.GetAsync(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/commands");
        var historyBody = await historyResponse.Content.ReadAsStringAsync();
        using var response = await client.GetAsync(MapChangePath);
        var body = await response.Content.ReadAsStringAsync();

        historyResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        historyBody.Should().NotContain("Change map");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("Operator role required");
        body.Should().Contain("RCON binding");
        body.Should().NotContain("map-change-form");
        body.Should().NotContain("ConfirmationToken");
    }

    [Theory]
    [InlineData(true, true, "Wait for the current command to finish")]
    [InlineData(false, false, "Configure the RCON binding first")]
    public async Task Map_change_form_is_locked_when_a_precondition_is_not_met(
        bool commandInProgress,
        bool rconConfigured,
        string expectedMessage)
    {
        await using var factory = new ReaderWebApplicationFactory(
            WebSecurity.OperatorRole,
            commandInProgress: commandInProgress,
            rconConfigured: rconConfigured);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(MapChangePath);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain(expectedMessage);
        body.Should().NotContain("map-change-form");
        body.Should().NotContain("ConfirmationToken");
    }

    [Fact]
    public async Task Operator_can_prepare_and_queue_one_bound_map_change()
    {
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        using var client = CreateNonRedirectingClient(factory);
        var review = await PrepareReviewAsync(client, "  de_dust2  ");

        review.Body.Should().Contain("Confirm map change");
        review.Body.Should().Contain("de_dust2");
        review.Body.Should().Contain("changelevel");
        review.Body.Should().NotContain("name=\"Model.Map\"");
        review.Body.Should().NotContain("name=\"Map\"");

        using var response = await client.PostAsync(
            QueuePath,
            CreateConfirmationForm(review, forgedMap: "de_inferno"));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().Be(new Uri(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/commands?result=map-change-queued",
            UriKind.Relative));
        factory.OperatorApiClient.MapChangeCallCount.Should().Be(1);
        factory.OperatorApiClient.LastMapChangeServerId.Should().Be(
            ReaderWebApplicationFactory.ServerId);
        factory.OperatorApiClient.LastMap.Should().Be("de_dust2");
        factory.OperatorApiClient.CallCount.Should().Be(0);
        factory.OperatorApiClient.RestartCallCount.Should().Be(0);
    }

    [Fact]
    public async Task Unsafe_map_name_never_reaches_review_or_operator_api()
    {
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        using var client = CreateNonRedirectingClient(factory);

        using var response = await PostMapForReviewAsync(client, "de_dust2;quit");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("Use only ASCII letters, digits, underscores, or hyphens");
        body.Should().Contain("map-change-form");
        body.Should().NotContain("map-change-confirmation");
        factory.OperatorApiClient.MapChangeCallCount.Should().Be(0);
    }

    [Fact]
    public async Task Confirmation_is_single_use_and_requires_explicit_acknowledgement()
    {
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        using var client = CreateNonRedirectingClient(factory);
        var review = await PrepareReviewAsync(client, "de_nuke");

        using var missingAcknowledgement = await client.PostAsync(
            QueuePath,
            CreateConfirmationForm(review, confirmed: false));
        using var accepted = await client.PostAsync(
            QueuePath,
            CreateConfirmationForm(review));
        using var repeated = await client.PostAsync(
            QueuePath,
            CreateConfirmationForm(review));

        missingAcknowledgement.Headers.Location.Should().Be(new Uri(
            $"{MapChangePath}?result=invalid",
            UriKind.Relative));
        accepted.Headers.Location.Should().Be(new Uri(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/commands?result=map-change-queued",
            UriKind.Relative));
        repeated.Headers.Location.Should().Be(new Uri(
            $"{MapChangePath}?result=confirmation-expired",
            UriKind.Relative));
        factory.OperatorApiClient.MapChangeCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Submit_rechecks_incomplete_commands_before_operator_api_call()
    {
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        using var client = CreateNonRedirectingClient(factory);
        var review = await PrepareReviewAsync(client, "de_train");
        factory.ReaderApiClient.CommandInProgress = true;

        using var response = await client.PostAsync(
            QueuePath,
            CreateConfirmationForm(review));

        response.Headers.Location.Should().Be(new Uri(
            $"{MapChangePath}?result=commands-in-progress",
            UriKind.Relative));
        factory.OperatorApiClient.MapChangeCallCount.Should().Be(0);
    }

    [Fact]
    public async Task Reader_cannot_submit_and_missing_antiforgery_is_rejected()
    {
        await using var readerFactory = new ReaderWebApplicationFactory();
        using var readerClient = CreateNonRedirectingClient(readerFactory);
        using var forged = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ConfirmationToken"] = new('a', OperatorMapChangeConfirmationStore.TokenLength),
            ["Confirmed"] = "true"
        });

        using var forbidden = await readerClient.PostAsync(QueuePath, forged);

        forbidden.StatusCode.Should().Be(HttpStatusCode.Redirect);
        forbidden.Headers.Location.Should().Be(new Uri("/auth/forbidden", UriKind.Relative));
        readerFactory.OperatorApiClient.MapChangeCallCount.Should().Be(0);

        await using var operatorFactory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        using var operatorClient = CreateNonRedirectingClient(operatorFactory);
        var review = await PrepareReviewAsync(operatorClient, "de_cbble");
        using var missingAntiforgery = new FormUrlEncodedContent(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ConfirmationToken"] = review.ConfirmationToken,
                ["Confirmed"] = "true"
            });

        using var rejected = await operatorClient.PostAsync(QueuePath, missingAntiforgery);

        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        operatorFactory.OperatorApiClient.MapChangeCallCount.Should().Be(0);
    }

    [Fact]
    public async Task Operator_without_subject_cannot_allocate_or_submit_confirmation()
    {
        await using var factory = new ReaderWebApplicationFactory(
            WebSecurity.OperatorRole,
            subject: null);
        using var client = CreateNonRedirectingClient(factory);

        using var pageResponse = await client.GetAsync(MapChangePath);
        var pageBody = await pageResponse.Content.ReadAsStringAsync();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ConfirmationToken"] = new('a', OperatorMapChangeConfirmationStore.TokenLength),
            ["Confirmed"] = "true"
        });
        using var postResponse = await client.PostAsync(QueuePath, content);

        pageResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        pageBody.Should().Contain("Operator role required");
        pageBody.Should().NotContain("map-change-form");
        postResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        postResponse.Headers.Location.Should().Be(new Uri("/auth/forbidden", UriKind.Relative));
        factory.OperatorApiClient.MapChangeCallCount.Should().Be(0);
    }

    [Theory]
    [InlineData((int)OperatorCommandQueueResult.ServerNotFound, "server-not-found")]
    [InlineData((int)OperatorCommandQueueResult.MissingRconCredential, "credential-missing")]
    [InlineData((int)OperatorCommandQueueResult.Rejected, "rejected")]
    public async Task Api_rejection_returns_to_fresh_review_without_retrying(
        int resultKind,
        string result)
    {
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        factory.OperatorApiClient.MapChangeResult = (OperatorCommandQueueResult)resultKind;
        using var client = CreateNonRedirectingClient(factory);
        var review = await PrepareReviewAsync(client, "de_aztec");

        using var response = await client.PostAsync(
            QueuePath,
            CreateConfirmationForm(review));

        response.Headers.Location.Should().Be(new Uri(
            $"{MapChangePath}?result={result}",
            UriKind.Relative));
        factory.OperatorApiClient.MapChangeCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Unknown_api_outcome_is_not_retried_and_redirects_to_history()
    {
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        factory.OperatorApiClient.MapChangeExceptionToThrow =
            new HttpRequestException("Simulated transport failure");
        using var client = CreateNonRedirectingClient(factory);
        var review = await PrepareReviewAsync(client, "de_inferno");

        using var response = await client.PostAsync(
            QueuePath,
            CreateConfirmationForm(review));

        response.Headers.Location.Should().Be(new Uri(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/commands?result=map-change-unknown",
            UriKind.Relative));
        factory.OperatorApiClient.MapChangeCallCount.Should().Be(1);
    }

    private static string MapChangePath =>
        $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/commands/change-map";

    private static string QueuePath => $"{MapChangePath}/queue";

    private static HttpClient CreateNonRedirectingClient(ReaderWebApplicationFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

    private static async Task<MapChangeReview> PrepareReviewAsync(
        HttpClient client,
        string map)
    {
        using var reviewResponse = await PostMapForReviewAsync(client, map);
        var reviewBody = await reviewResponse.Content.ReadAsStringAsync();
        reviewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        reviewBody.Should().Contain("map-change-confirmation");

        return new MapChangeReview(
            reviewBody,
            GetHiddenInputValue(reviewBody, "__RequestVerificationToken"),
            GetHiddenInputValue(reviewBody, "ConfirmationToken"));
    }

    private static async Task<HttpResponseMessage> PostMapForReviewAsync(
        HttpClient client,
        string map)
    {
        using var pageResponse = await client.GetAsync(MapChangePath);
        var pageBody = await pageResponse.Content.ReadAsStringAsync();
        pageResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        pageBody.Should().Contain("map-change-form");
        pageBody.Should().Contain("name=\"_handler\" value=\"map-change\"");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["__RequestVerificationToken"] = GetHiddenInputValue(
                pageBody,
                "__RequestVerificationToken"),
            ["_handler"] = "map-change",
            ["Model.Map"] = map
        });

        return await client.PostAsync(MapChangePath, form);
    }

    private static FormUrlEncodedContent CreateConfirmationForm(
        MapChangeReview review,
        bool confirmed = true,
        string? forgedMap = null)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["__RequestVerificationToken"] = review.AntiforgeryToken,
            ["ConfirmationToken"] = review.ConfirmationToken
        };
        if (confirmed)
        {
            values["Confirmed"] = "true";
        }

        if (forgedMap is not null)
        {
            values["Map"] = forgedMap;
        }

        return new FormUrlEncodedContent(values);
    }

    private static string GetHiddenInputValue(string html, string name)
    {
        var nameMarker = $"name=\"{name}\"";
        var nameIndex = html.IndexOf(nameMarker, StringComparison.Ordinal);
        nameIndex.Should().BeGreaterThanOrEqualTo(0);
        var tagStart = html.LastIndexOf('<', nameIndex);
        var tagEnd = html.IndexOf('>', nameIndex);
        var input = html[tagStart..tagEnd];
        const string valueMarker = "value=\"";
        var valueStart = input.IndexOf(valueMarker, StringComparison.Ordinal) + valueMarker.Length;
        var valueEnd = input.IndexOf('"', valueStart);
        return WebUtility.HtmlDecode(input[valueStart..valueEnd]);
    }

    private sealed record MapChangeReview(
        string Body,
        string AntiforgeryToken,
        string ConfirmationToken);
}
