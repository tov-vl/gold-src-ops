using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using GoldSrcOps.Application.Alerts;
using GoldSrcOps.Application.Common;
using GoldSrcOps.Contracts.Alerts;
using GoldSrcOps.Domain.Servers;
using GoldSrcOps.Infrastructure.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GoldSrcOps.UnitTests.Api;

public sealed class PostgreSqlDeadLetterReplayEndpointIntegrationTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Replay_persists_audit_and_resets_only_mutable_delivery_state()
    {
        var now = new DateTimeOffset(2026, 8, 26, 18, 0, 0, TimeSpan.Zero);
        var clock = new TestClock(now);
        await using var factory = await CreateFactoryAsync(clock);
        using var client = factory.CreateClient();
        var deadLetteredAtUtc = now.AddMinutes(-30);
        var seeded = await SeedDeadLetterAsync(
            factory,
            now.AddHours(-1),
            deadLetteredAtUtc,
            "permanent HTTP 400 response");
        var requestId = Guid.NewGuid();

        using var response = await ReplayAsync(
            client,
            seeded.EventId,
            requestId,
            " downstream endpoint was corrected ");

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.OriginalString.Should().Be(
            $"/api/alert-delivery/replays/{requestId:D}");
        var replay = await response.Content.ReadFromJsonAsync<DeadLetterReplayResponse>();
        replay.Should().NotBeNull();
        replay.Should().BeEquivalentTo(new DeadLetterReplayResponse(
            requestId,
            seeded.EventId,
            "operator-42",
            now,
            "downstream endpoint was corrected",
            ReplayNumber: 1,
            PreviousAttemptCount: 1,
            deadLetteredAtUtc,
            Status: "Pending",
            NextAttemptAtUtc: now));

        var persisted = await ReadPersistenceSnapshotAsync(factory, seeded.EventId, requestId);
        persisted.Message.Should().BeEquivalentTo(new MessageSnapshot(
            seeded.EventId,
            seeded.EventType,
            seeded.PayloadVersion,
            IncidentAlertEvents.AggregateType,
            seeded.IncidentId,
            seeded.OccurredAtUtc,
            seeded.Payload,
            OutboxMessageStatus.Pending,
            AttemptCount: 0,
            ReplayCount: 1,
            NextAttemptAtUtc: now,
            ClaimId: null,
            ClaimedAtUtc: null,
            ProcessedAtUtc: null,
            LastError: null,
            DeadLetteredAtUtc: null));
        persisted.Request.Should().BeEquivalentTo(new ReplayRequestSnapshot(
            requestId,
            seeded.EventId,
            seeded.EventType,
            seeded.PayloadVersion,
            IncidentAlertEvents.AggregateType,
            seeded.IncidentId,
            seeded.OccurredAtUtc,
            "operator-42",
            now,
            "downstream endpoint was corrected",
            ReplayNumber: 1,
            PreviousAttemptCount: 1,
            deadLetteredAtUtc,
            PreviousLastError: "permanent HTTP 400 response",
            NextAttemptAtUtc: now));

        using var getResponse = await client.GetAsync(
            $"/api/alert-delivery/replays/{requestId:D}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var durableReplay = await getResponse.Content.ReadFromJsonAsync<DeadLetterReplayResponse>();
        durableReplay.Should().BeEquivalentTo(replay);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Same_key_retry_returns_original_result_without_resetting_a_dispatcher_claim()
    {
        var now = new DateTimeOffset(2026, 8, 26, 18, 30, 0, TimeSpan.Zero);
        var clock = new TestClock(now);
        await using var factory = await CreateFactoryAsync(clock);
        using var client = factory.CreateClient();
        var seeded = await SeedDeadLetterAsync(
            factory,
            now.AddHours(-1),
            now.AddMinutes(-20),
            "delivery failed");
        var requestId = Guid.NewGuid();

        using var accepted = await ReplayAsync(
            client,
            seeded.EventId,
            requestId,
            "endpoint restored");
        accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var claim = await ClaimNextAsync(factory, now);
        claim.Should().NotBeNull();
        claim!.Id.Should().Be(seeded.EventId);
        clock.UtcNow = now.AddMinutes(5);

        using var repeated = await ReplayAsync(
            client,
            seeded.EventId,
            requestId,
            " endpoint restored ");
        using var changedIntent = await ReplayAsync(
            client,
            seeded.EventId,
            requestId,
            "a different reason");

        repeated.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var repeatedReplay = await repeated.Content.ReadFromJsonAsync<DeadLetterReplayResponse>();
        repeatedReplay.Should().NotBeNull();
        repeatedReplay!.RequestedAtUtc.Should().Be(now);
        repeatedReplay.ReplayNumber.Should().Be(1);
        changedIntent.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadProblemCodeAsync(changedIntent)).Should().Be(
            "alert_delivery.idempotency_conflict");

        var persisted = await ReadPersistenceSnapshotAsync(factory, seeded.EventId, requestId);
        persisted.Message.Status.Should().Be(OutboxMessageStatus.Processing);
        persisted.Message.ClaimId.Should().Be(claim.ClaimId);
        persisted.Message.ReplayCount.Should().Be(1);
        persisted.RequestCount.Should().Be(1);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Concurrent_requests_enforce_one_transition_and_same_key_convergence()
    {
        var now = new DateTimeOffset(2026, 8, 26, 19, 0, 0, TimeSpan.Zero);
        await using var factory = await CreateFactoryAsync(new TestClock(now));
        using var client = factory.CreateClient();
        var seeded = await SeedDeadLetterAsync(
            factory,
            now.AddHours(-1),
            now.AddMinutes(-15),
            "delivery failed");

        var responses = await Task.WhenAll(
            ReplayAsync(client, seeded.EventId, Guid.NewGuid(), "endpoint restored"),
            ReplayAsync(client, seeded.EventId, Guid.NewGuid(), "endpoint restored"));

        try
        {
            responses.Count(static response => response.StatusCode == HttpStatusCode.Accepted)
                .Should()
                .Be(1);
            responses.Count(static response => response.StatusCode == HttpStatusCode.Conflict)
                .Should()
                .Be(1);
            var conflict = responses.Single(static response =>
                response.StatusCode == HttpStatusCode.Conflict);
            (await ReadProblemCodeAsync(conflict)).Should().Be(
                "alert_delivery.event_not_dead_letter");

            var aggregate = await factory.ExecuteDbContextAsync(async dbContext =>
                new
                {
                    Message = await dbContext.OutboxMessages
                        .AsNoTracking()
                        .SingleAsync(message => message.Id == seeded.EventId),
                    RequestCount = await dbContext.OutboxReplayRequests.CountAsync()
                });
            aggregate.Message.Status.Should().Be(OutboxMessageStatus.Pending);
            aggregate.Message.ReplayCount.Should().Be(1);
            aggregate.RequestCount.Should().Be(1);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        var firstClaim = await ClaimNextAsync(factory, now);
        firstClaim.Should().NotBeNull();
        firstClaim!.Id.Should().Be(seeded.EventId);
        await MarkProcessedAsync(factory, firstClaim, now.AddMinutes(1));
        var repeatedTarget = await SeedDeadLetterAsync(
            factory,
            now.AddMinutes(2),
            now.AddMinutes(3),
            "delivery failed again");
        var repeatedRequestId = Guid.NewGuid();

        var repeatedResponses = await Task.WhenAll(
            ReplayAsync(
                client,
                repeatedTarget.EventId,
                repeatedRequestId,
                "endpoint restored"),
            ReplayAsync(
                client,
                repeatedTarget.EventId,
                repeatedRequestId,
                "endpoint restored"));

        try
        {
            repeatedResponses.Should().OnlyContain(static response =>
                response.StatusCode == HttpStatusCode.Accepted);
            var results = await Task.WhenAll(repeatedResponses.Select(response =>
                response.Content.ReadFromJsonAsync<DeadLetterReplayResponse>()));
            results.Should().OnlyContain(result =>
                result != null &&
                result.RequestId == repeatedRequestId &&
                result.EventId == repeatedTarget.EventId &&
                result.ReplayNumber == 1);

            var converged = await factory.ExecuteDbContextAsync(async dbContext =>
                new
                {
                    Message = await dbContext.OutboxMessages
                        .AsNoTracking()
                        .SingleAsync(message => message.Id == repeatedTarget.EventId),
                    RequestCount = await dbContext.OutboxReplayRequests.CountAsync()
                });
            converged.Message.Status.Should().Be(OutboxMessageStatus.Pending);
            converged.Message.ReplayCount.Should().Be(1);
            converged.RequestCount.Should().Be(2);
        }
        finally
        {
            foreach (var response in repeatedResponses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Same_key_competing_across_events_is_bound_to_one_intent()
    {
        var now = new DateTimeOffset(2026, 8, 26, 19, 30, 0, TimeSpan.Zero);
        await using var factory = await CreateFactoryAsync(new TestClock(now));
        using var client = factory.CreateClient();
        var first = await SeedDeadLetterAsync(
            factory,
            now.AddHours(-2),
            now.AddMinutes(-30),
            "first delivery failed");
        var second = await SeedDeadLetterAsync(
            factory,
            now.AddHours(-1),
            now.AddMinutes(-20),
            "second delivery failed");
        var requestId = Guid.NewGuid();

        var responses = await Task.WhenAll(
            ReplayAsync(client, first.EventId, requestId, "endpoint restored"),
            ReplayAsync(client, second.EventId, requestId, "endpoint restored"));

        try
        {
            responses.Count(static response => response.StatusCode == HttpStatusCode.Accepted)
                .Should()
                .Be(1);
            responses.Count(static response => response.StatusCode == HttpStatusCode.Conflict)
                .Should()
                .Be(1);
            var conflict = responses.Single(static response =>
                response.StatusCode == HttpStatusCode.Conflict);
            (await ReadProblemCodeAsync(conflict)).Should().Be(
                "alert_delivery.idempotency_conflict");

            var aggregate = await factory.ExecuteDbContextAsync(async dbContext =>
                new
                {
                    Messages = await dbContext.OutboxMessages
                        .AsNoTracking()
                        .OrderBy(message => message.Id)
                        .ToListAsync(),
                    RequestCount = await dbContext.OutboxReplayRequests.CountAsync()
                });
            aggregate.Messages.Count(static message =>
                    message.Status == OutboxMessageStatus.Pending && message.ReplayCount == 1)
                .Should()
                .Be(1);
            aggregate.Messages.Count(static message =>
                    message.Status == OutboxMessageStatus.DeadLetter && message.ReplayCount == 0)
                .Should()
                .Be(1);
            aggregate.RequestCount.Should().Be(1);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Replay_rejects_missing_non_dead_letter_and_newer_processing_events()
    {
        var now = new DateTimeOffset(2026, 8, 26, 20, 0, 0, TimeSpan.Zero);
        await using var factory = await CreateFactoryAsync(new TestClock(now));
        using var client = factory.CreateClient();
        var target = await SeedDeadLetterAsync(
            factory,
            now.AddHours(-2),
            now.AddMinutes(-30),
            "delivery failed");
        var newerEventId = await AddPendingMessageAsync(
            factory,
            target.IncidentId,
            now.AddHours(-1),
            IncidentAlertEvents.ServerRecovered,
            "{\"sequence\":2}");
        var newerClaim = await ClaimNextAsync(factory, now);
        newerClaim.Should().NotBeNull();
        newerClaim!.Id.Should().Be(newerEventId);

        using var missing = await ReplayAsync(
            client,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "endpoint restored");
        using var nonDeadLetter = await ReplayAsync(
            client,
            newerEventId,
            Guid.NewGuid(),
            "endpoint restored");
        using var unsafeReplay = await ReplayAsync(
            client,
            target.EventId,
            Guid.NewGuid(),
            "endpoint restored");

        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadProblemCodeAsync(missing)).Should().Be(
            "alert_delivery.event_not_found");
        nonDeadLetter.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadProblemCodeAsync(nonDeadLetter)).Should().Be(
            "alert_delivery.event_not_dead_letter");
        unsafeReplay.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadProblemCodeAsync(unsafeReplay)).Should().Be(
            "alert_delivery.newer_event_processing");

        var state = await factory.ExecuteDbContextAsync(async dbContext =>
            new
            {
                Target = await dbContext.OutboxMessages
                    .AsNoTracking()
                    .SingleAsync(message => message.Id == target.EventId),
                RequestCount = await dbContext.OutboxReplayRequests.CountAsync()
            });
        state.Target.Status.Should().Be(OutboxMessageStatus.DeadLetter);
        state.Target.ReplayCount.Should().Be(0);
        state.RequestCount.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Audit_insert_failure_rolls_back_outbox_transition()
    {
        var now = new DateTimeOffset(2026, 8, 26, 20, 30, 0, TimeSpan.Zero);
        await using var factory = await CreateFactoryAsync(new TestClock(now));
        var deadLetteredAtUtc = now.AddMinutes(-30);
        var seeded = await SeedDeadLetterAsync(
            factory,
            now.AddHours(-1),
            deadLetteredAtUtc,
            "delivery failed");
        await factory.ExecuteDbContextAsync(async dbContext =>
            await dbContext.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION goldsrcops.reject_replay_audit()
                RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'forced replay audit failure';
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER reject_replay_audit
                BEFORE INSERT ON goldsrcops.outbox_replay_requests
                FOR EACH ROW EXECUTE FUNCTION goldsrcops.reject_replay_audit();
                """));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var replayService = scope.ServiceProvider.GetRequiredService<AlertDeliveryReplayService>();
            var act = () => replayService.ReplayAsync(
                new DeadLetterReplayCommand(
                    Guid.NewGuid(),
                    seeded.EventId,
                    "operator-42",
                    "endpoint restored"),
                CancellationToken.None);

            await act.Should().ThrowAsync<DbUpdateException>();
        }

        var state = await factory.ExecuteDbContextAsync(async dbContext =>
            new
            {
                Message = await dbContext.OutboxMessages
                    .AsNoTracking()
                    .SingleAsync(message => message.Id == seeded.EventId),
                RequestCount = await dbContext.OutboxReplayRequests.CountAsync()
            });
        state.Message.Status.Should().Be(OutboxMessageStatus.DeadLetter);
        state.Message.AttemptCount.Should().Be(1);
        state.Message.ReplayCount.Should().Be(0);
        state.Message.DeadLetteredAtUtc.Should().Be(deadLetteredAtUtc);
        state.Message.LastError.Should().Be("delivery failed");
        state.RequestCount.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Concurrent_incident_close_preserves_aggregate_dispatch_order()
    {
        var now = new DateTimeOffset(2026, 8, 26, 21, 0, 0, TimeSpan.Zero);
        await using var factory = await CreateFactoryAsync(new TestClock(now));
        using var client = factory.CreateClient();
        var target = await SeedDeadLetterAsync(
            factory,
            now.AddHours(-2),
            now.AddMinutes(-30),
            "delivery failed");
        var recoveredEventId = Guid.NewGuid();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<GoldSrcOps.Infrastructure.Persistence.GoldSrcOpsDbContext>();
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted);
            var incident = (await dbContext.AvailabilityIncidents
                .FromSqlInterpolated($$"""
                    SELECT *
                    FROM "goldsrcops"."availability_incidents"
                    WHERE "Id" = {{target.IncidentId}}
                    FOR UPDATE
                    """)
                .ToListAsync()).Single();
            incident.Close(now.AddMinutes(-5), "server query recovered");
            dbContext.OutboxMessages.Add(new OutboxMessage(
                recoveredEventId,
                IncidentAlertEvents.ServerRecovered,
                payloadVersion: 1,
                IncidentAlertEvents.AggregateType,
                target.IncidentId,
                now.AddMinutes(-5),
                "{\"sequence\":2}"));
            await dbContext.SaveChangesAsync();

            var replayTask = ReplayAsync(
                client,
                target.EventId,
                Guid.NewGuid(),
                "endpoint restored");
            await Task.Delay(TimeSpan.FromMilliseconds(200));
            replayTask.IsCompleted.Should().BeFalse();

            await transaction.CommitAsync();
            using var replay = await replayTask;
            replay.StatusCode.Should().Be(HttpStatusCode.Accepted);
        }

        var targetClaim = await ClaimNextAsync(factory, now);
        targetClaim.Should().NotBeNull();
        targetClaim!.Id.Should().Be(target.EventId);
        (await ClaimNextAsync(factory, now)).Should().BeNull();
        await MarkProcessedAsync(factory, targetClaim, now.AddMinutes(1));
        var recoveredClaim = await ClaimNextAsync(factory, now.AddMinutes(1));
        recoveredClaim.Should().NotBeNull();
        recoveredClaim!.Id.Should().Be(recoveredEventId);
    }

    private static Task<PostgreSqlGoldSrcOpsApiFactory> CreateFactoryAsync(TestClock clock) =>
        PostgreSqlGoldSrcOpsApiFactory.CreateAsync(
            services =>
            {
                services.RemoveAll<IClock>();
                services.AddSingleton<IClock>(clock);
            },
            TestApiPrincipal.Operator("operator-42"));

    private static async Task<SeededDeadLetter> SeedDeadLetterAsync(
        PostgreSqlGoldSrcOpsApiFactory factory,
        DateTimeOffset occurredAtUtc,
        DateTimeOffset deadLetteredAtUtc,
        string lastError)
    {
        var seeded = await factory.ExecuteDbContextAsync(async dbContext =>
        {
            var server = new Server(
                "Dust2 Public",
                GameServerKind.GoldSrc,
                new ServerEndpoint("127.0.0.1", queryPort: 27015, rconPort: null),
                pollIntervalSeconds: 30,
                notes: null,
                occurredAtUtc.AddHours(-1));
            var incident = AvailabilityIncident.Open(
                server.Id,
                occurredAtUtc.AddMinutes(-1),
                "server query failed",
                consecutiveFailures: 3);
            var message = new OutboxMessage(
                Guid.NewGuid(),
                IncidentAlertEvents.ServerUnavailable,
                payloadVersion: 1,
                IncidentAlertEvents.AggregateType,
                incident.Id,
                occurredAtUtc,
                "{\"sequence\":1}");
            dbContext.Servers.Add(server);
            dbContext.AvailabilityIncidents.Add(incident);
            dbContext.OutboxMessages.Add(message);
            await dbContext.SaveChangesAsync();

            return new SeededDeadLetter(
                message.Id,
                incident.Id,
                message.EventType,
                message.PayloadVersion,
                message.OccurredAtUtc,
                message.Payload);
        });

        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var claim = await store.ClaimNextPendingAsync(
            occurredAtUtc.AddMinutes(1),
            CancellationToken.None);
        claim.Should().NotBeNull();
        claim!.Id.Should().Be(seeded.EventId);
        (await store.MarkDeadLetterAsync(
            seeded.EventId,
            claim.ClaimId,
            deadLetteredAtUtc,
            lastError,
            CancellationToken.None)).Should().BeTrue();

        var persistedPayload = await factory.ExecuteDbContextAsync(async dbContext =>
            await dbContext.OutboxMessages
                .AsNoTracking()
                .Where(message => message.Id == seeded.EventId)
                .Select(static message => message.Payload)
                .SingleAsync());

        return seeded with { Payload = persistedPayload };
    }

    private static async Task<Guid> AddPendingMessageAsync(
        PostgreSqlGoldSrcOpsApiFactory factory,
        Guid incidentId,
        DateTimeOffset occurredAtUtc,
        string eventType,
        string payload)
    {
        return await factory.ExecuteDbContextAsync(async dbContext =>
        {
            var message = new OutboxMessage(
                Guid.NewGuid(),
                eventType,
                payloadVersion: 1,
                IncidentAlertEvents.AggregateType,
                incidentId,
                occurredAtUtc,
                payload);
            dbContext.OutboxMessages.Add(message);
            await dbContext.SaveChangesAsync();
            return message.Id;
        });
    }

    private static async Task<HttpResponseMessage> ReplayAsync(
        HttpClient client,
        Guid eventId,
        Guid requestId,
        string reason)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/alert-delivery/dead-letters/{eventId:D}/replay")
        {
            Content = JsonContent.Create(new ReplayDeadLetterRequest(reason))
        };
        request.Headers.Add("Idempotency-Key", requestId.ToString("D"));
        return await client.SendAsync(request);
    }

    private static async Task<string> ReadProblemCodeAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("code").GetString()!;
    }

    private static async Task<ClaimedOutboxMessage?> ClaimNextAsync(
        PostgreSqlGoldSrcOpsApiFactory factory,
        DateTimeOffset claimedAtUtc)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        return await store.ClaimNextPendingAsync(claimedAtUtc, CancellationToken.None);
    }

    private static async Task MarkProcessedAsync(
        PostgreSqlGoldSrcOpsApiFactory factory,
        ClaimedOutboxMessage message,
        DateTimeOffset processedAtUtc)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        (await store.MarkProcessedAsync(
            message.Id,
            message.ClaimId,
            processedAtUtc,
            CancellationToken.None)).Should().BeTrue();
    }

    private static Task<PersistenceSnapshot> ReadPersistenceSnapshotAsync(
        PostgreSqlGoldSrcOpsApiFactory factory,
        Guid eventId,
        Guid requestId) =>
        factory.ExecuteDbContextAsync(async dbContext =>
        {
            var message = await dbContext.OutboxMessages
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == eventId);
            var request = await dbContext.OutboxReplayRequests
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == requestId);

            return new PersistenceSnapshot(
                new MessageSnapshot(
                    message.Id,
                    message.EventType,
                    message.PayloadVersion,
                    message.AggregateType,
                    message.AggregateId,
                    message.OccurredAtUtc,
                    message.Payload,
                    message.Status,
                    message.AttemptCount,
                    message.ReplayCount,
                    message.NextAttemptAtUtc,
                    message.ClaimId,
                    message.ClaimedAtUtc,
                    message.ProcessedAtUtc,
                    message.LastError,
                    message.DeadLetteredAtUtc),
                new ReplayRequestSnapshot(
                    request.Id,
                    request.OutboxMessageId,
                    request.EventType,
                    request.PayloadVersion,
                    request.AggregateType,
                    request.AggregateId,
                    request.OccurredAtUtc,
                    request.RequestedBy,
                    request.RequestedAtUtc,
                    request.Reason,
                    request.ReplayNumber,
                    request.PreviousAttemptCount,
                    request.PreviousDeadLetteredAtUtc,
                    request.PreviousLastError,
                    request.NextAttemptAtUtc),
                await dbContext.OutboxReplayRequests.CountAsync());
        });

    private sealed record SeededDeadLetter(
        Guid EventId,
        Guid IncidentId,
        string EventType,
        short PayloadVersion,
        DateTimeOffset OccurredAtUtc,
        string Payload);

    private sealed record PersistenceSnapshot(
        MessageSnapshot Message,
        ReplayRequestSnapshot Request,
        int RequestCount);

    private sealed record MessageSnapshot(
        Guid Id,
        string EventType,
        short PayloadVersion,
        string AggregateType,
        Guid AggregateId,
        DateTimeOffset OccurredAtUtc,
        string Payload,
        OutboxMessageStatus Status,
        int AttemptCount,
        int ReplayCount,
        DateTimeOffset NextAttemptAtUtc,
        Guid? ClaimId,
        DateTimeOffset? ClaimedAtUtc,
        DateTimeOffset? ProcessedAtUtc,
        string? LastError,
        DateTimeOffset? DeadLetteredAtUtc);

    private sealed record ReplayRequestSnapshot(
        Guid Id,
        Guid OutboxMessageId,
        string EventType,
        short PayloadVersion,
        string AggregateType,
        Guid AggregateId,
        DateTimeOffset OccurredAtUtc,
        string RequestedBy,
        DateTimeOffset RequestedAtUtc,
        string Reason,
        int ReplayNumber,
        int PreviousAttemptCount,
        DateTimeOffset? PreviousDeadLetteredAtUtc,
        string? PreviousLastError,
        DateTimeOffset NextAttemptAtUtc);

    private sealed class TestClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }
}
