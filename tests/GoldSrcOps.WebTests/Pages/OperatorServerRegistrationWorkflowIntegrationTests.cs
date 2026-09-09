using System.Net;
using AwesomeAssertions;
using GoldSrcOps.Web.Security;
using GoldSrcOps.Web.Services;
using Microsoft.AspNetCore.Mvc.Testing;

namespace GoldSrcOps.WebTests.Pages;

public sealed class OperatorServerRegistrationWorkflowIntegrationTests
{
    [Fact]
    public async Task Reader_cannot_discover_or_open_server_registration()
    {
        await using var factory = new ReaderWebApplicationFactory();
        using var client = CreateNonRedirectingClient(factory);

        using var inventoryResponse = await client.GetAsync("/operator/servers");
        var inventoryBody = await inventoryResponse.Content.ReadAsStringAsync();
        using var registrationResponse = await client.GetAsync("/operator/servers/new");

        inventoryResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        inventoryBody.Should().NotContain("Register server");
        registrationResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        registrationResponse.Headers.Location.Should().Be(new Uri("/auth/forbidden", UriKind.Relative));
    }

    [Fact]
    public async Task Reader_cannot_submit_a_forged_registration_confirmation()
    {
        await using var factory = new ReaderWebApplicationFactory();
        using var client = CreateNonRedirectingClient(factory);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ConfirmationToken"] = new('a', 43),
            ["Confirmed"] = "true"
        });

        using var response = await client.PostAsync("/operator/servers/registrations", content);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().Be(new Uri("/auth/forbidden", UriKind.Relative));
        factory.OperatorApiClient.RegistrationCallCount.Should().Be(0);
    }

    [Fact]
    public async Task Operator_can_review_and_confirm_one_paused_registration()
    {
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        using var client = CreateNonRedirectingClient(factory);
        var review = await PrepareReviewAsync(client);

        review.Body.Should().Contain("Confirm the paused registration");
        review.Body.Should().Contain("game-new.example.test:27015");
        review.Body.Should().Contain("Paused");
        review.Body.Should().Contain("Not configured");
        review.Body.Should().NotContain("type=\"password\"");

        using var response = await client.PostAsync(
            "/operator/servers/registrations",
            CreateConfirmationForm(review));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().Be(new Uri(
            $"/operator/servers/{ReaderWebApplicationFactory.ServerId:D}?result=server-registered",
            UriKind.Relative));
        factory.OperatorApiClient.RegistrationCallCount.Should().Be(1);
        factory.OperatorApiClient.LastRegistrationDraft.Should().NotBeNull();
        factory.OperatorApiClient.LastRegistrationDraft!.RequestId.Should().NotBe(Guid.Empty);
        factory.OperatorApiClient.LastRegistrationDraft.Should().BeEquivalentTo(
            new
            {
                Name = "New public server",
                Host = "game-new.example.test",
                QueryPort = 27015,
                RconPort = (int?)27016,
                PollIntervalSeconds = 45,
                Notes = "Awaiting monitoring approval"
            });
    }

    [Fact]
    public async Task Confirmation_is_single_use_and_requires_explicit_acknowledgement()
    {
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        using var client = CreateNonRedirectingClient(factory);
        var review = await PrepareReviewAsync(client);

        using var missingAcknowledgement = await client.PostAsync(
            "/operator/servers/registrations",
            CreateConfirmationForm(review, confirmed: false));
        using var accepted = await client.PostAsync(
            "/operator/servers/registrations",
            CreateConfirmationForm(review));
        using var repeated = await client.PostAsync(
            "/operator/servers/registrations",
            CreateConfirmationForm(review));

        missingAcknowledgement.StatusCode.Should().Be(HttpStatusCode.Redirect);
        missingAcknowledgement.Headers.Location.Should().Be(new Uri(
            "/operator/servers/new?result=invalid",
            UriKind.Relative));
        accepted.StatusCode.Should().Be(HttpStatusCode.Redirect);
        repeated.StatusCode.Should().Be(HttpStatusCode.Redirect);
        repeated.Headers.Location.Should().Be(new Uri(
            "/operator/servers/new?result=confirmation-expired",
            UriKind.Relative));
        factory.OperatorApiClient.RegistrationCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Missing_antiforgery_token_is_rejected_before_registration()
    {
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        using var client = CreateNonRedirectingClient(factory);
        var review = await PrepareReviewAsync(client);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ConfirmationToken"] = review.ConfirmationToken,
            ["Confirmed"] = "true"
        });

        using var response = await client.PostAsync("/operator/servers/registrations", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        factory.OperatorApiClient.RegistrationCallCount.Should().Be(0);
    }

    [Fact]
    public async Task Unknown_api_outcome_is_not_retried_and_returns_to_inventory()
    {
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        factory.OperatorApiClient.RegistrationExceptionToThrow =
            new HttpRequestException("Simulated transport failure");
        using var client = CreateNonRedirectingClient(factory);
        var review = await PrepareReviewAsync(client);

        using var response = await client.PostAsync(
            "/operator/servers/registrations",
            CreateConfirmationForm(review));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().Be(new Uri(
            "/operator/servers?result=registration-unknown",
            UriKind.Relative));
        factory.OperatorApiClient.RegistrationCallCount.Should().Be(1);
    }

    private static HttpClient CreateNonRedirectingClient(ReaderWebApplicationFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

    private static async Task<RegistrationReview> PrepareReviewAsync(HttpClient client)
    {
        using var pageResponse = await client.GetAsync("/operator/servers/new");
        var pageBody = await pageResponse.Content.ReadAsStringAsync();
        pageResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        pageBody.Should().Contain("registration-form");
        pageBody.Should().Contain("__RequestVerificationToken");
        pageBody.Should().Contain("name=\"_handler\" value=\"server-registration\"");
        pageBody.Should().NotContain("type=\"password\"");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["__RequestVerificationToken"] = GetHiddenInputValue(
                pageBody,
                "__RequestVerificationToken"),
            ["_handler"] = "server-registration",
            ["Model.Name"] = "  New public server  ",
            ["Model.Host"] = "  game-new.example.test  ",
            ["Model.QueryPort"] = "27015",
            ["Model.RconPort"] = "27016",
            ["Model.PollIntervalSeconds"] = "45",
            ["Model.Notes"] = "  Awaiting monitoring approval  "
        });

        using var reviewResponse = await client.PostAsync("/operator/servers/new", form);
        var reviewBody = await reviewResponse.Content.ReadAsStringAsync();
        reviewResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        return new RegistrationReview(
            reviewBody,
            GetHiddenInputValue(reviewBody, "__RequestVerificationToken"),
            GetHiddenInputValue(reviewBody, "ConfirmationToken"));
    }

    private static FormUrlEncodedContent CreateConfirmationForm(
        RegistrationReview review,
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

    private sealed record RegistrationReview(
        string Body,
        string AntiforgeryToken,
        string ConfirmationToken);
}
