using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using GoldSrcOps.Contracts.GameEvents;
using GoldSrcOps.GameEventAgent;

namespace GoldSrcOps.UnitTests.GameEventAgent;

public sealed class GameEventDeliveryClientTests
{
    [Theory]
    [InlineData(HttpStatusCode.Accepted, false, (int)GameEventDeliveryOutcome.Accepted)]
    [InlineData(HttpStatusCode.OK, true, (int)GameEventDeliveryOutcome.Idempotent)]
    public async Task SendAsync_acknowledges_only_matching_receipt(
        HttpStatusCode statusCode,
        bool duplicate,
        int expectedOutcome)
    {
        var options = GameEventAgentTestData.CreateDeliveryOptions();
        var gameEvent = CreateQueuedEvent();
        var receipt = new GameEventIngestResponse(
            gameEvent.EventId,
            options.ServerId,
            gameEvent.SourceInstanceId,
            gameEvent.SequenceNumber,
            gameEvent.ContractVersion,
            gameEvent.Type,
            gameEvent.OccurredAtUtc,
            GameEventAgentTestData.NowUtc + TimeSpan.FromSeconds(1),
            duplicate);
        using var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(StubHttpMessageHandler.JsonResponse(
                statusCode,
                JsonSerializer.Serialize(receipt, GameEventJson.SerializerOptions))));
        var tokenProvider = new StubAccessTokenProvider();
        var sut = new GameEventDeliveryClient(
            new StubHttpClientFactory(handler, options.ApiBaseUri),
            tokenProvider,
            options);

        var result = await sut.SendAsync(gameEvent, CancellationToken.None);

        result.Outcome.Should().Be((GameEventDeliveryOutcome)expectedOutcome);
        result.Failure.Should().Be(GameEventDeliveryFailure.None);
        handler.LastAuthorizationScheme.Should().Be("Bearer");
        handler.LastRequestUri.Should().Be(
            new Uri(options.ApiBaseUri, $"api/servers/{options.ServerId:D}/game-events"));
        handler.LastRequestBody.Should().Equal(gameEvent.PayloadUtf8);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, (int)GameEventDeliveryOutcome.PermanentFailure, (int)GameEventDeliveryFailure.Conflict)]
    [InlineData(HttpStatusCode.BadRequest, (int)GameEventDeliveryOutcome.PermanentFailure, (int)GameEventDeliveryFailure.InvalidRequest)]
    [InlineData(HttpStatusCode.ServiceUnavailable, (int)GameEventDeliveryOutcome.RetryableFailure, (int)GameEventDeliveryFailure.RemoteServer)]
    [InlineData(HttpStatusCode.TooManyRequests, (int)GameEventDeliveryOutcome.RetryableFailure, (int)GameEventDeliveryFailure.Throttled)]
    [InlineData(HttpStatusCode.Forbidden, (int)GameEventDeliveryOutcome.RetryableFailure, (int)GameEventDeliveryFailure.Forbidden)]
    public async Task SendAsync_classifies_non_success_response(
        HttpStatusCode statusCode,
        int expectedOutcome,
        int expectedFailure)
    {
        var options = GameEventAgentTestData.CreateDeliveryOptions();
        using var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(statusCode)));
        var sut = new GameEventDeliveryClient(
            new StubHttpClientFactory(handler, options.ApiBaseUri),
            new StubAccessTokenProvider(),
            options);

        var result = await sut.SendAsync(CreateQueuedEvent(), CancellationToken.None);

        result.Outcome.Should().Be((GameEventDeliveryOutcome)expectedOutcome);
        result.Failure.Should().Be((GameEventDeliveryFailure)expectedFailure);
    }

    [Fact]
    public async Task SendAsync_invalidates_cached_token_after_unauthorized_response()
    {
        var options = GameEventAgentTestData.CreateDeliveryOptions();
        using var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var tokenProvider = new StubAccessTokenProvider();
        var sut = new GameEventDeliveryClient(
            new StubHttpClientFactory(handler, options.ApiBaseUri),
            tokenProvider,
            options);

        var result = await sut.SendAsync(CreateQueuedEvent(), CancellationToken.None);

        result.Outcome.Should().Be(GameEventDeliveryOutcome.RetryableFailure);
        result.Failure.Should().Be(GameEventDeliveryFailure.Unauthorized);
        tokenProvider.Invalidations.Should().Be(1);
    }

    [Fact]
    public async Task SendAsync_keeps_event_when_success_receipt_does_not_match()
    {
        var options = GameEventAgentTestData.CreateDeliveryOptions();
        var gameEvent = CreateQueuedEvent();
        var mismatchedReceipt = new GameEventIngestResponse(
            Guid.NewGuid(),
            options.ServerId,
            gameEvent.SourceInstanceId,
            gameEvent.SequenceNumber,
            gameEvent.ContractVersion,
            gameEvent.Type,
            gameEvent.OccurredAtUtc,
            GameEventAgentTestData.NowUtc,
            Duplicate: false);
        using var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(StubHttpMessageHandler.JsonResponse(
                HttpStatusCode.Accepted,
                JsonSerializer.Serialize(mismatchedReceipt, GameEventJson.SerializerOptions))));
        var sut = new GameEventDeliveryClient(
            new StubHttpClientFactory(handler, options.ApiBaseUri),
            new StubAccessTokenProvider(),
            options);

        var result = await sut.SendAsync(gameEvent, CancellationToken.None);

        result.Outcome.Should().Be(GameEventDeliveryOutcome.PermanentFailure);
        result.Failure.Should().Be(GameEventDeliveryFailure.InvalidReceipt);
    }

    private static QueuedGameEvent CreateQueuedEvent()
    {
        var eventId = Guid.Parse("527ee0bb-0102-44b2-82f7-7e1a3ed10db1");
        var sourceInstanceId = Guid.Parse("680fd9c5-84af-41d7-8660-ed5cb8728345");
        var request = new GameEventIngestRequest(
            GameEventContractRules.ContractVersion,
            eventId,
            sourceInstanceId,
            42,
            "round.ended",
            GameEventAgentTestData.NowUtc,
            "de_dust2",
            12,
            0);
        return new QueuedGameEvent(
            eventId,
            sourceInstanceId,
            42,
            GameEventContractRules.ContractVersion,
            "round.ended",
            GameEventAgentTestData.NowUtc,
            "de_dust2",
            12,
            0,
            GameEventJson.SerializeRequest(request),
            GameEventAgentTestData.NowUtc,
            AttemptCount: 1);
    }
}
