using System.Net;
using AwesomeAssertions;
using GoldSrcOps.Web.Security;
using GoldSrcOps.Web.Services;
using Microsoft.AspNetCore.Mvc.Testing;

namespace GoldSrcOps.WebTests.Pages;

public sealed class OperatorServerUpdateWorkflowIntegrationTests
{
    [Fact]
    public async Task Reader_can_inspect_configuration_but_not_edit_controls()
    {
        await using var factory = new ReaderWebApplicationFactory(serverEnabled: false);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(SettingsPath);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("Current server configuration");
        body.Should().Contain("game.example.test");
        body.Should().Contain("Operator role required");
        body.Should().NotContain("settings-form");
        body.Should().NotContain("ConfirmationToken");
        body.Should().NotContain("type=\"password\"");
    }

    [Fact]
    public async Task Operator_must_pause_monitoring_before_editing()
    {
        await using var factory = new ReaderWebApplicationFactory(
            WebSecurity.OperatorRole,
            serverEnabled: true);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(SettingsPath);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("Pause monitoring before editing");
        body.Should().Contain($"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/monitoring");
        body.Should().NotContain("settings-form");
        body.Should().NotContain("ConfirmationToken");
    }

    [Fact]
    public async Task Operator_can_review_and_confirm_one_paused_update()
    {
        await using var factory = new ReaderWebApplicationFactory(
            WebSecurity.OperatorRole,
            serverEnabled: false);
        using var client = CreateNonRedirectingClient(factory);
        var review = await PrepareReviewAsync(client);

        review.Body.Should().Contain("Confirm configuration revision 7");
        review.Body.Should().Contain("updated.example.test:27016");
        review.Body.Should().Contain("Remains paused");
        review.Body.Should().Contain("Unchanged");
        review.Body.Should().NotContain("type=\"password\"");

        using var response = await client.PostAsync(ApplyPath, CreateConfirmationForm(review));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().Be(new Uri(
            $"{SettingsPath}?result=updated",
            UriKind.Relative));
        factory.OperatorApiClient.UpdateCallCount.Should().Be(1);
        factory.OperatorApiClient.LastUpdateDraft.Should().BeEquivalentTo(new
        {
            ServerId = ReaderWebApplicationFactory.ServerId,
            ExpectedRevision = 7L,
            Name = "Updated fixture server",
            Host = "updated.example.test",
            QueryPort = 27016,
            RconPort = (int?)27017,
            PollIntervalSeconds = 45,
            Notes = "Reviewed metadata change"
        });
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

        missingAcknowledgement.StatusCode.Should().Be(HttpStatusCode.Redirect);
        missingAcknowledgement.Headers.Location.Should().Be(new Uri(
            $"{SettingsPath}?result=invalid",
            UriKind.Relative));
        accepted.StatusCode.Should().Be(HttpStatusCode.Redirect);
        repeated.StatusCode.Should().Be(HttpStatusCode.Redirect);
        repeated.Headers.Location.Should().Be(new Uri(
            $"{SettingsPath}?result=confirmation-expired",
            UriKind.Relative));
        factory.OperatorApiClient.UpdateCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Reader_cannot_submit_a_forged_update_confirmation()
    {
        await using var factory = new ReaderWebApplicationFactory(serverEnabled: false);
        using var client = CreateNonRedirectingClient(factory);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ConfirmationToken"] = new('a', 43),
            ["Confirmed"] = "true"
        });

        using var response = await client.PostAsync(ApplyPath, content);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().Be(new Uri("/auth/forbidden", UriKind.Relative));
        factory.OperatorApiClient.UpdateCallCount.Should().Be(0);
    }

    [Fact]
    public async Task Missing_antiforgery_token_is_rejected_before_update()
    {
        await using var factory = new ReaderWebApplicationFactory(
            WebSecurity.OperatorRole,
            serverEnabled: false);
        using var client = CreateNonRedirectingClient(factory);
        var review = await PrepareReviewAsync(client);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ConfirmationToken"] = review.ConfirmationToken,
            ["Confirmed"] = "true"
        });

        using var response = await client.PostAsync(ApplyPath, content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        factory.OperatorApiClient.UpdateCallCount.Should().Be(0);
    }

    [Theory]
    [InlineData((int)OperatorServerUpdateResultKind.Conflict, "conflict")]
    [InlineData((int)OperatorServerUpdateResultKind.Rejected, "rejected")]
    [InlineData((int)OperatorServerUpdateResultKind.ServerNotFound, "server-not-found")]
    public async Task Api_rejection_is_not_retried_and_returns_to_fresh_configuration(
        int resultKind,
        string result)
    {
        await using var factory = new ReaderWebApplicationFactory(
            WebSecurity.OperatorRole,
            serverEnabled: false);
        factory.OperatorApiClient.UpdateResult = new OperatorServerUpdateResult(
            (OperatorServerUpdateResultKind)resultKind,
            Server: null);
        using var client = CreateNonRedirectingClient(factory);
        var review = await PrepareReviewAsync(client);

        using var response = await client.PostAsync(ApplyPath, CreateConfirmationForm(review));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().Be(new Uri(
            $"{SettingsPath}?result={result}",
            UriKind.Relative));
        factory.OperatorApiClient.UpdateCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Unknown_api_outcome_is_not_retried_and_returns_to_fresh_configuration()
    {
        await using var factory = new ReaderWebApplicationFactory(
            WebSecurity.OperatorRole,
            serverEnabled: false);
        factory.OperatorApiClient.UpdateExceptionToThrow =
            new HttpRequestException("Simulated transport failure");
        using var client = CreateNonRedirectingClient(factory);
        var review = await PrepareReviewAsync(client);

        using var response = await client.PostAsync(ApplyPath, CreateConfirmationForm(review));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().Be(new Uri(
            $"{SettingsPath}?result=update-unknown",
            UriKind.Relative));
        factory.OperatorApiClient.UpdateCallCount.Should().Be(1);
    }

    private static string SettingsPath =>
        $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}/settings";

    private static string ApplyPath => $"{SettingsPath}/apply";

    private static HttpClient CreateNonRedirectingClient(ReaderWebApplicationFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

    private static async Task<UpdateReview> PrepareReviewAsync(HttpClient client)
    {
        using var pageResponse = await client.GetAsync(SettingsPath);
        var pageBody = await pageResponse.Content.ReadAsStringAsync();
        pageResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        pageBody.Should().Contain("settings-form");
        pageBody.Should().Contain("__RequestVerificationToken");
        pageBody.Should().Contain("name=\"_handler\" value=\"server-settings\"");
        pageBody.Should().NotContain("type=\"password\"");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["__RequestVerificationToken"] = GetHiddenInputValue(
                pageBody,
                "__RequestVerificationToken"),
            ["_handler"] = "server-settings",
            ["Model.ExpectedRevision"] = "7",
            ["Model.Name"] = "  Updated fixture server  ",
            ["Model.Host"] = "  updated.example.test  ",
            ["Model.QueryPort"] = "27016",
            ["Model.RconPort"] = "27017",
            ["Model.PollIntervalSeconds"] = "45",
            ["Model.Notes"] = "  Reviewed metadata change  "
        });

        using var reviewResponse = await client.PostAsync(SettingsPath, form);
        var reviewBody = await reviewResponse.Content.ReadAsStringAsync();
        reviewResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        return new UpdateReview(
            reviewBody,
            GetHiddenInputValue(reviewBody, "__RequestVerificationToken"),
            GetHiddenInputValue(reviewBody, "ConfirmationToken"));
    }

    private static FormUrlEncodedContent CreateConfirmationForm(
        UpdateReview review,
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

    private sealed record UpdateReview(
        string Body,
        string AntiforgeryToken,
        string ConfirmationToken);
}
