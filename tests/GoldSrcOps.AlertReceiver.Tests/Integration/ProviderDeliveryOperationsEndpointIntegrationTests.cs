using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using GoldSrcOps.AlertReceiver.AvailabilityEvents;
using GoldSrcOps.AlertReceiver.Configuration;
using GoldSrcOps.AlertReceiver.Persistence;
using GoldSrcOps.AlertReceiver.ProviderDelivery;
using GoldSrcOps.Application.Alerts;
using GoldSrcOps.Contracts.ProviderDelivery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GoldSrcOps.AlertReceiver.Tests.Integration;

[Collection(AlertReceiverPostgreSqlTestGroup.CollectionName)]
public sealed class ProviderDeliveryOperationsEndpointIntegrationTests(
    AlertReceiverPostgreSqlFixture database)
    : IAsyncLifetime
{
    private static readonly Guid ServerId =
        Guid.Parse("83d4df9e-7e1d-449a-b343-2b58df14a6b5");

    private static readonly DateTimeOffset OpenedAtUtc =
        new(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);

    public Task InitializeAsync() => database.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Disabled_operations_surface_is_hidden()
    {
        await using var factory = await AlertReceiverFactory.CreateAsync(
            database.ConnectionString,
            ReceiverMode.Live,
            providerOperationsEnabled: false);
        using var client = factory.CreateClient();
        using var request = CreateOperationsRequest(
            HttpMethod.Get,
            "/internal/v1/provider-delivery/dead-letters");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync())
            .Should().NotContain(AlertReceiverFactory.OperationsAuthorization);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Operations_surface_requires_separate_authorization()
    {
        await using var factory = await AlertReceiverFactory.CreateAsync(
            database.ConnectionString,
            ReceiverMode.Live);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/internal/v1/provider-delivery/dead-letters");
        request.Headers.Add("Authorization", AlertReceiverFactory.Authorization);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync())
            .Should().NotContain(AlertReceiverFactory.OperationsAuthorization);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Dead_letter_list_is_bounded_sanitized_and_stably_paged()
    {
        await using var factory = await AlertReceiverFactory.CreateAsync(
            database.ConnectionString,
            ReceiverMode.Live);
        using var client = factory.CreateClient();
        var first = await CreateDeadLetterAsync(
            factory,
            client,
            DateTimeOffset.UtcNow.AddMinutes(10),
            "Synthetic provider failure one.");
        var second = await CreateDeadLetterAsync(
            factory,
            client,
            DateTimeOffset.UtcNow.AddMinutes(11),
            "Synthetic provider failure two.");

        using var firstPageRequest = CreateOperationsRequest(
            HttpMethod.Get,
            "/internal/v1/provider-delivery/dead-letters?limit=1");
        using var firstPageResponse = await client.SendAsync(firstPageRequest);
        var firstPageBody = await firstPageResponse.Content.ReadAsStringAsync();
        var firstPage = JsonSerializer.Deserialize<ProviderDeadLetterListResponse>(
            firstPageBody,
            JsonSerializerOptions.Web)!;

        firstPageResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        firstPage.Limit.Should().Be(1);
        firstPage.Items.Should().ContainSingle();
        firstPage.Items[0].MessageId.Should().Be(second.MessageId);
        firstPage.NextCursor.Should().NotBeNullOrWhiteSpace();
        firstPageBody.Should().NotContain("\"payload\"");
        firstPageBody.Should().NotContain("GoldSrcOps Controlled CS 1.6");
        firstPageBody.Should().NotContain(AlertReceiverFactory.Authorization);
        firstPageBody.Should().NotContain(AlertReceiverFactory.OperationsAuthorization);

        using var secondPageRequest = CreateOperationsRequest(
            HttpMethod.Get,
            $"/internal/v1/provider-delivery/dead-letters?limit=1&cursor={Uri.EscapeDataString(firstPage.NextCursor!)}");
        using var secondPageResponse = await client.SendAsync(secondPageRequest);
        var secondPage = await secondPageResponse.Content
            .ReadFromJsonAsync<ProviderDeadLetterListResponse>();

        secondPageResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        secondPage.Should().NotBeNull();
        secondPage!.Items.Should().ContainSingle();
        secondPage.Items[0].MessageId.Should().Be(first.MessageId);
        secondPage.NextCursor.Should().BeNull();
    }

    [Theory]
    [InlineData("?limit=0")]
    [InlineData("?limit=101")]
    [InlineData("?cursor=not-a-cursor")]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Invalid_list_bounds_are_rejected(string query)
    {
        await using var factory = await AlertReceiverFactory.CreateAsync(
            database.ConnectionString,
            ReceiverMode.Live);
        using var client = factory.CreateClient();
        using var request = CreateOperationsRequest(
            HttpMethod.Get,
            $"/internal/v1/provider-delivery/dead-letters{query}");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Review_is_append_only_and_exact_retries_are_idempotent()
    {
        await using var factory = await AlertReceiverFactory.CreateAsync(
            database.ConnectionString,
            ReceiverMode.Live);
        using var client = factory.CreateClient();
        var deadLetter = await CreateDeadLetterAsync(
            factory,
            client,
            DateTimeOffset.UtcNow.AddMinutes(10),
            "Synthetic provider failure.");
        var requestId = Guid.NewGuid();
        var reviewRequest = new ProviderDeadLetterReviewRequest(
            "operator-42",
            "Reviewed stale provider trigger after bounded investigation.");

        using var acceptedRequest = CreateReviewRequest(deadLetter.MessageId, requestId, reviewRequest);
        using var acceptedResponse = await client.SendAsync(acceptedRequest);
        var accepted = await acceptedResponse.Content
            .ReadFromJsonAsync<ProviderDeadLetterReviewResponse>();
        using var duplicateRequest = CreateReviewRequest(deadLetter.MessageId, requestId, reviewRequest);
        using var duplicateResponse = await client.SendAsync(duplicateRequest);
        var duplicate = await duplicateResponse.Content
            .ReadFromJsonAsync<ProviderDeadLetterReviewResponse>();
        using var getRequest = CreateOperationsRequest(
            HttpMethod.Get,
            $"/internal/v1/provider-delivery/reviews/{requestId:D}");
        using var getResponse = await client.SendAsync(getRequest);
        var persisted = await getResponse.Content
            .ReadFromJsonAsync<ProviderDeadLetterReviewResponse>();

        acceptedResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        duplicateResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        accepted.Should().Be(duplicate).And.Be(persisted);
        accepted!.RequestId.Should().Be(requestId);
        accepted.MessageId.Should().Be(deadLetter.MessageId);

        var state = await factory.ExecuteDbContextAsync(async dbContext => new
        {
            Status = await dbContext.ProviderOutboxMessages
                .Where(message => message.Id == deadLetter.MessageId)
                .Select(message => message.Status)
                .SingleAsync(),
            Reviews = await dbContext.ProviderOutboxReviews.CountAsync(),
        });
        state.Status.Should().Be(ProviderOutboxStatus.DeadLetter);
        state.Reviews.Should().Be(1);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Reused_request_or_already_reviewed_message_is_rejected_without_mutation()
    {
        await using var factory = await AlertReceiverFactory.CreateAsync(
            database.ConnectionString,
            ReceiverMode.Live);
        using var client = factory.CreateClient();
        var deadLetter = await CreateDeadLetterAsync(
            factory,
            client,
            DateTimeOffset.UtcNow.AddMinutes(10),
            "Synthetic provider failure.");
        var requestId = Guid.NewGuid();
        var reviewRequest = new ProviderDeadLetterReviewRequest(
            "operator-42",
            "Reviewed after bounded investigation.");
        using (var acceptedRequest = CreateReviewRequest(deadLetter.MessageId, requestId, reviewRequest))
        using (var acceptedResponse = await client.SendAsync(acceptedRequest))
        {
            acceptedResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        }

        using var changedRequest = CreateReviewRequest(
            deadLetter.MessageId,
            requestId,
            reviewRequest with { Reason = "Changed reason." });
        using var changedResponse = await client.SendAsync(changedRequest);
        using var secondReviewRequest = CreateReviewRequest(
            deadLetter.MessageId,
            Guid.NewGuid(),
            reviewRequest);
        using var secondReviewResponse = await client.SendAsync(secondReviewRequest);

        changedResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadProblemCodeAsync(changedResponse))
            .Should().Be("provider_delivery.idempotency_conflict");
        secondReviewResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadProblemCodeAsync(secondReviewResponse))
            .Should().Be("provider_delivery.already_reviewed");
        (await factory.ExecuteDbContextAsync(db => db.ProviderOutboxReviews.CountAsync()))
            .Should().Be(1);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Pending_message_cannot_be_reviewed()
    {
        await using var factory = await AlertReceiverFactory.CreateAsync(
            database.ConnectionString,
            ReceiverMode.Live);
        using var client = factory.CreateClient();
        var unavailable = CreateUnavailable();
        using var ingestion = await SendAvailabilityAsync(client, unavailable);
        ingestion.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var messageId = await factory.ExecuteDbContextAsync(db =>
            db.ProviderOutboxMessages.Select(message => message.Id).SingleAsync());
        using var request = CreateReviewRequest(
            messageId,
            Guid.NewGuid(),
            new ProviderDeadLetterReviewRequest(
                "operator-42",
                "This must not be accepted while delivery is pending."));

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadProblemCodeAsync(response))
            .Should().Be("provider_delivery.message_not_dead_letter");
        (await factory.ExecuteDbContextAsync(db => db.ProviderOutboxReviews.CountAsync()))
            .Should().Be(0);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Concurrent_exact_reviews_create_one_record()
    {
        await using var factory = await AlertReceiverFactory.CreateAsync(
            database.ConnectionString,
            ReceiverMode.Live);
        using var client = factory.CreateClient();
        var deadLetter = await CreateDeadLetterAsync(
            factory,
            client,
            DateTimeOffset.UtcNow.AddMinutes(10),
            "Synthetic provider failure.");
        var requestId = Guid.NewGuid();
        var reviewRequest = new ProviderDeadLetterReviewRequest(
            "operator-42",
            "Concurrent exact review request.");

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            using var request = CreateReviewRequest(deadLetter.MessageId, requestId, reviewRequest);
            return await client.SendAsync(request);
        }));

        try
        {
            responses.Count(response => response.StatusCode == HttpStatusCode.Accepted)
                .Should().Be(1);
            responses.Count(response => response.StatusCode == HttpStatusCode.OK)
                .Should().Be(7);
            (await factory.ExecuteDbContextAsync(db => db.ProviderOutboxReviews.CountAsync()))
                .Should().Be(1);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    private static async Task<DeadLetterFixture> CreateDeadLetterAsync(
        AlertReceiverFactory factory,
        HttpClient client,
        DateTimeOffset deadLetteredAtUtc,
        string failure)
    {
        var unavailable = CreateUnavailable();
        using var ingestion = await SendAvailabilityAsync(client, unavailable);
        ingestion.StatusCode.Should().Be(HttpStatusCode.Accepted);

        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderOutboxStore>();
        var claimed = await store.ClaimNextAsync(
            deadLetteredAtUtc.AddMinutes(-1),
            CancellationToken.None);
        claimed.Should().NotBeNull();
        (await store.MarkDeadLetterAsync(
            claimed!.Id,
            claimed.ClaimId,
            deadLetteredAtUtc,
            failure,
            CancellationToken.None)).Should().BeTrue();

        return new DeadLetterFixture(claimed.Id, unavailable.EventId, unavailable.IncidentId);
    }

    private static AvailabilityEventRequest CreateUnavailable() =>
        new(
            Guid.NewGuid(),
            IncidentAlertEvents.ServerUnavailable,
            OpenedAtUtc,
            Guid.NewGuid(),
            ServerId,
            "GoldSrcOps Controlled CS 1.6",
            "Query timed out after 3000 ms.",
            ConsecutiveFailures: 3,
            OpenedAtUtc,
            ClosedAtUtc: null,
            DurationSeconds: null,
            IncidentAlertEventV1.CurrentPayloadVersion);

    private static Task<HttpResponseMessage> SendAvailabilityAsync(
        HttpClient client,
        AvailabilityEventRequest request)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/availability-events")
        {
            Content = JsonContent.Create(request),
        };
        message.Headers.Add("Authorization", AlertReceiverFactory.Authorization);
        message.Headers.Add("Idempotency-Key", request.EventId.ToString("D"));
        return client.SendAsync(message);
    }

    private static HttpRequestMessage CreateReviewRequest(
        Guid messageId,
        Guid requestId,
        ProviderDeadLetterReviewRequest request)
    {
        var message = CreateOperationsRequest(
            HttpMethod.Post,
            $"/internal/v1/provider-delivery/dead-letters/{messageId:D}/review",
            JsonContent.Create(request));
        message.Headers.Add("Idempotency-Key", requestId.ToString("D"));
        return message;
    }

    private static HttpRequestMessage CreateOperationsRequest(
        HttpMethod method,
        string path,
        HttpContent? content = null)
    {
        var message = new HttpRequestMessage(method, path)
        {
            Content = content,
        };
        message.Headers.Add("Authorization", AlertReceiverFactory.OperationsAuthorization);
        return message;
    }

    private static async Task<string?> ReadProblemCodeAsync(HttpResponseMessage response)
    {
        var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        return problem?["code"]?.GetValue<string>();
    }

    private sealed record DeadLetterFixture(
        Guid MessageId,
        Guid SourceEventId,
        Guid IncidentId);
}
