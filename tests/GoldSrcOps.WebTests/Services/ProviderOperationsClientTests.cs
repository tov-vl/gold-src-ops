using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using GoldSrcOps.Contracts.ProviderDelivery;
using GoldSrcOps.Web.Services;

namespace GoldSrcOps.WebTests.Services;

public sealed class ProviderOperationsClientTests
{
    private static readonly Guid MessageId = Guid.Parse("6d78ab5d-76b2-4777-a193-f2956cd7346c");
    private static readonly Guid RequestId = Guid.Parse("72dd41ca-10a8-4108-b678-18fe290afee5");

    [Fact]
    public async Task GetDeadLettersAsync_uses_bounded_escaped_query()
    {
        var page = new ProviderDeadLetterListResponse(25, null, []);
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(page)
        });
        var client = CreateClient(handler);

        var result = await client.GetDeadLettersAsync("cursor/value+next", 25);

        result.Should().BeEquivalentTo(page);
        handler.Method.Should().Be(HttpMethod.Get);
        handler.RequestUri.Should().Be(new Uri(
            "https://receiver.example.test/internal/v1/provider-delivery/dead-letters?limit=25&cursor=cursor%2Fvalue%2Bnext"));
    }

    [Fact]
    public async Task ReviewDeadLetterAsync_posts_identity_reason_and_idempotency_key()
    {
        var review = CreateReview();
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.Accepted)
        {
            Content = JsonContent.Create(review)
        });
        var client = CreateClient(handler);

        var result = await client.ReviewDeadLetterAsync(
            MessageId,
            RequestId,
            "operator-subject",
            "Reviewed terminal provider response");

        result.Should().Be(new ProviderDeadLetterReviewResult(
            ProviderDeadLetterReviewResultKind.Accepted,
            review));
        handler.Method.Should().Be(HttpMethod.Post);
        handler.RequestUri.Should().Be(new Uri(
            $"https://receiver.example.test/internal/v1/provider-delivery/dead-letters/{MessageId:D}/review"));
        handler.IdempotencyKey.Should().Be(RequestId.ToString("D"));
        handler.ReviewRequest.Should().Be(new ProviderDeadLetterReviewRequest(
            "operator-subject",
            "Reviewed terminal provider response"));
    }

    [Theory]
    [InlineData("provider_delivery.message_not_dead_letter", (int)ProviderDeadLetterReviewResultKind.NotDeadLetter)]
    [InlineData("provider_delivery.idempotency_conflict", (int)ProviderDeadLetterReviewResultKind.IdempotencyConflict)]
    [InlineData("provider_delivery.already_reviewed", (int)ProviderDeadLetterReviewResultKind.AlreadyReviewed)]
    [InlineData("provider_delivery.unknown", (int)ProviderDeadLetterReviewResultKind.Rejected)]
    public async Task ReviewDeadLetterAsync_maps_conflict_codes(
        string code,
        int expected)
    {
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = JsonContent.Create(new { code })
        });
        var client = CreateClient(handler);

        var result = await client.ReviewDeadLetterAsync(
            MessageId,
            RequestId,
            "operator-subject",
            "Reviewed");

        result.Kind.Should().Be((ProviderDeadLetterReviewResultKind)expected);
    }

    [Fact]
    public async Task GetDeadLetterAsync_rejects_mismatched_success_record()
    {
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(CreateMessage(Guid.NewGuid()))
        });
        var client = CreateClient(handler);

        var action = () => client.GetDeadLetterAsync(MessageId);

        await action.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*mismatched dead-letter message*");
    }

    [Fact]
    public async Task GetReviewAsync_returns_null_for_not_found()
    {
        var client = CreateClient(new CaptureHandler(new HttpResponseMessage(HttpStatusCode.NotFound)));

        var result = await client.GetReviewAsync(RequestId);

        result.Should().BeNull();
    }

    private static ProviderOperationsClient CreateClient(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://receiver.example.test/") });

    private static ProviderDeadLetterReviewResponse CreateReview() =>
        new(
            RequestId,
            MessageId,
            "operator-subject",
            "Reviewed terminal provider response",
            new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero));

    private static ProviderDeadLetterListItemResponse CreateMessage(Guid messageId) =>
        new(
            messageId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Trigger",
            DateTimeOffset.UtcNow.AddMinutes(-5),
            4,
            DateTimeOffset.UtcNow,
            "Terminal provider response",
            null);

    private sealed class CaptureHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? IdempotencyKey { get; private set; }
        public ProviderDeadLetterReviewRequest? ReviewRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            if (request.Headers.TryGetValues("Idempotency-Key", out var values))
            {
                IdempotencyKey = values.Single();
            }

            if (request.Content is not null)
            {
                ReviewRequest = await request.Content.ReadFromJsonAsync<ProviderDeadLetterReviewRequest>(
                    cancellationToken);
            }

            return response;
        }
    }
}
