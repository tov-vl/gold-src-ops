using System.Net;
using AwesomeAssertions;
using GoldSrcOps.Web.Security;
using GoldSrcOps.Web.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace GoldSrcOps.WebTests.Pages;

public sealed class OperatorRconCredentialWorkflowIntegrationTests
{
    [Fact]
    public async Task Reader_can_inspect_sanitized_metadata_but_not_binding_controls()
    {
        await using var factory = new ReaderWebApplicationFactory(serverEnabled: false);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(CredentialsPath);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("Current RCON credential metadata");
        body.Should().Contain("Configured");
        body.Should().Contain("Reference only");
        body.Should().Contain("Operator role required");
        body.Should().NotContain("credential-form");
        body.Should().NotContain("primary_server");
        body.Should().NotContain("type=\"password\"");
    }

    [Fact]
    public async Task Operator_must_pause_monitoring_before_binding()
    {
        await using var factory = new ReaderWebApplicationFactory(
            WebSecurity.OperatorRole,
            serverEnabled: true);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(CredentialsPath);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("Pause monitoring before rebinding");
        body.Should().Contain($"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/monitoring");
        body.Should().NotContain("credential-form");
        body.Should().NotContain("ConfirmationToken");
    }

    [Fact]
    public async Task Operator_can_review_and_confirm_one_paused_binding()
    {
        await using var factory = new ReaderWebApplicationFactory(
            WebSecurity.OperatorRole,
            serverEnabled: false);
        using var client = CreateNonRedirectingClient(factory);
        var review = await PrepareReviewAsync(client);

        review.Body.Should().Contain("Confirm RCON credential binding");
        review.Body.Should().Contain("primary_server");
        review.Body.Should().Contain("Expected credential revision");
        review.Body.Should().Contain(">3<");
        review.Body.Should().Contain("Expected server revision");
        review.Body.Should().Contain(">7<");
        review.Body.Should().Contain("Raw secret").And.Contain("Not submitted");
        review.Body.Should().NotContain("type=\"password\"");

        using var response = await client.PostAsync(ApplyPath, CreateConfirmationForm(review));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().Be(new Uri(
            $"{CredentialsPath}?result=updated",
            UriKind.Relative));
        factory.OperatorApiClient.CredentialUpdateCallCount.Should().Be(1);
        factory.OperatorApiClient.LastCredentialUpdateDraft.Should().Be(
            new OperatorRconCredentialDraft(
                ReaderWebApplicationFactory.ServerId,
                ExpectedServerRevision: 7,
                ExpectedCredentialRevision: 3,
                SecretAlias: "primary_server"));
    }

    [Fact]
    public async Task Confirmation_is_single_use_and_requires_explicit_acknowledgement()
    {
        await using var factory = new ReaderWebApplicationFactory(
            WebSecurity.OperatorRole,
            serverEnabled: false);
        using var client = CreateNonRedirectingClient(factory);
        var review = await PrepareReviewAsync(client);

        using var missingAcknowledgement = await client.PostAsync(
            ApplyPath,
            CreateConfirmationForm(review, confirmed: false));
        using var accepted = await client.PostAsync(
            ApplyPath,
            CreateConfirmationForm(review));
        using var repeated = await client.PostAsync(
            ApplyPath,
            CreateConfirmationForm(review));

        missingAcknowledgement.Headers.Location.Should().Be(new Uri(
            $"{CredentialsPath}?result=invalid",
            UriKind.Relative));
        accepted.Headers.Location.Should().Be(new Uri(
            $"{CredentialsPath}?result=updated",
            UriKind.Relative));
        repeated.Headers.Location.Should().Be(new Uri(
            $"{CredentialsPath}?result=confirmation-expired",
            UriKind.Relative));
        factory.OperatorApiClient.CredentialUpdateCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Reader_cannot_submit_forged_binding_and_missing_antiforgery_is_rejected()
    {
        await using var readerFactory = new ReaderWebApplicationFactory(serverEnabled: false);
        using var readerClient = CreateNonRedirectingClient(readerFactory);
        using var forged = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ConfirmationToken"] = new('a', 43),
            ["Confirmed"] = "true"
        });

        using var forbidden = await readerClient.PostAsync(ApplyPath, forged);

        forbidden.StatusCode.Should().Be(HttpStatusCode.Redirect);
        forbidden.Headers.Location.Should().Be(new Uri("/auth/forbidden", UriKind.Relative));
        readerFactory.OperatorApiClient.CredentialUpdateCallCount.Should().Be(0);

        await using var operatorFactory = new ReaderWebApplicationFactory(
            WebSecurity.OperatorRole,
            serverEnabled: false);
        using var operatorClient = CreateNonRedirectingClient(operatorFactory);
        var review = await PrepareReviewAsync(operatorClient);
        using var missingAntiforgery = new FormUrlEncodedContent(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ConfirmationToken"] = review.ConfirmationToken,
                ["Confirmed"] = "true"
            });

        using var rejected = await operatorClient.PostAsync(ApplyPath, missingAntiforgery);

        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        operatorFactory.OperatorApiClient.CredentialUpdateCallCount.Should().Be(0);
    }

    [Fact]
    public async Task Reader_cannot_allocate_confirmation_through_a_forged_review_post()
    {
        await using var factory = new ReaderWebApplicationFactory(serverEnabled: false);
        using var client = factory.CreateClient();
        using var pageResponse = await client.GetAsync(CredentialsPath);
        var pageBody = await pageResponse.Content.ReadAsStringAsync();
        using var forgedReview = new FormUrlEncodedContent(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["__RequestVerificationToken"] = GetHiddenInputValue(
                    pageBody,
                    "__RequestVerificationToken"),
                ["_handler"] = "rcon-credential",
                ["Model.ExpectedServerRevision"] = "7",
                ["Model.ExpectedCredentialRevision"] = "3",
                ["Model.SecretAlias"] = "reader_forged"
            });

        using var response = await client.PostAsync(CredentialsPath, forgedReview);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var store = factory.Services.GetRequiredService<OperatorRconCredentialConfirmationStore>();
        var draft = new OperatorRconCredentialDraft(
            ReaderWebApplicationFactory.ServerId,
            ExpectedServerRevision: 7,
            ExpectedCredentialRevision: 3,
            SecretAlias: "operator_reserved");
        for (var index = 0; index < OperatorRconCredentialConfirmationStore.Capacity; index++)
        {
            store.Issue("operator-subject", draft).Should().NotBeNull();
        }
    }

    [Theory]
    [InlineData((int)OperatorRconCredentialUpdateResultKind.MonitoringEnabled, "monitoring-enabled")]
    [InlineData((int)OperatorRconCredentialUpdateResultKind.CommandsInProgress, "commands-in-progress")]
    [InlineData((int)OperatorRconCredentialUpdateResultKind.Conflict, "conflict")]
    [InlineData((int)OperatorRconCredentialUpdateResultKind.Rejected, "rejected")]
    [InlineData((int)OperatorRconCredentialUpdateResultKind.ServerNotFound, "server-not-found")]
    public async Task Api_rejection_is_not_retried_and_returns_to_fresh_metadata(
        int resultKind,
        string result)
    {
        await using var factory = new ReaderWebApplicationFactory(
            WebSecurity.OperatorRole,
            serverEnabled: false);
        factory.OperatorApiClient.CredentialUpdateResult = new OperatorRconCredentialUpdateResult(
            (OperatorRconCredentialUpdateResultKind)resultKind,
            Credential: null);
        using var client = CreateNonRedirectingClient(factory);
        var review = await PrepareReviewAsync(client);

        using var response = await client.PostAsync(ApplyPath, CreateConfirmationForm(review));

        response.Headers.Location.Should().Be(new Uri(
            $"{CredentialsPath}?result={result}",
            UriKind.Relative));
        factory.OperatorApiClient.CredentialUpdateCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Unknown_api_outcome_is_not_retried()
    {
        await using var factory = new ReaderWebApplicationFactory(
            WebSecurity.OperatorRole,
            serverEnabled: false);
        factory.OperatorApiClient.CredentialUpdateExceptionToThrow =
            new HttpRequestException("Simulated transport failure");
        using var client = CreateNonRedirectingClient(factory);
        var review = await PrepareReviewAsync(client);

        using var response = await client.PostAsync(ApplyPath, CreateConfirmationForm(review));

        response.Headers.Location.Should().Be(new Uri(
            $"{CredentialsPath}?result=update-unknown",
            UriKind.Relative));
        factory.OperatorApiClient.CredentialUpdateCallCount.Should().Be(1);
    }

    private static string CredentialsPath =>
        $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/credentials";

    private static string ApplyPath => $"{CredentialsPath}/apply";

    private static HttpClient CreateNonRedirectingClient(ReaderWebApplicationFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

    private static async Task<CredentialReview> PrepareReviewAsync(HttpClient client)
    {
        using var pageResponse = await client.GetAsync(CredentialsPath);
        var pageBody = await pageResponse.Content.ReadAsStringAsync();
        pageResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        pageBody.Should().Contain("credential-form");
        pageBody.Should().Contain("name=\"_handler\" value=\"rcon-credential\"");
        pageBody.Should().NotContain("type=\"password\"");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["__RequestVerificationToken"] = GetHiddenInputValue(
                pageBody,
                "__RequestVerificationToken"),
            ["_handler"] = "rcon-credential",
            ["Model.ExpectedServerRevision"] = "7",
            ["Model.ExpectedCredentialRevision"] = "3",
            ["Model.SecretAlias"] = "primary_server"
        });

        using var reviewResponse = await client.PostAsync(CredentialsPath, form);
        var reviewBody = await reviewResponse.Content.ReadAsStringAsync();
        reviewResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        return new CredentialReview(
            reviewBody,
            GetHiddenInputValue(reviewBody, "__RequestVerificationToken"),
            GetHiddenInputValue(reviewBody, "ConfirmationToken"));
    }

    private static FormUrlEncodedContent CreateConfirmationForm(
        CredentialReview review,
        bool confirmed = true)
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

    private sealed record CredentialReview(
        string Body,
        string AntiforgeryToken,
        string ConfirmationToken);
}
