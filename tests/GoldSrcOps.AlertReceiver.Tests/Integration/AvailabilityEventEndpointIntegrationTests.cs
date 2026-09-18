using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using GoldSrcOps.AlertReceiver.AvailabilityEvents;
using GoldSrcOps.AlertReceiver.Configuration;
using GoldSrcOps.AlertReceiver.Persistence;
using GoldSrcOps.AlertReceiver.ProviderDelivery;
using GoldSrcOps.AlertReceiver.Telemetry;
using GoldSrcOps.Application.Alerts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GoldSrcOps.AlertReceiver.Tests.Integration;

[Collection(AlertReceiverPostgreSqlTestGroup.CollectionName)]
public sealed class AvailabilityEventEndpointIntegrationTests(
    AlertReceiverPostgreSqlFixture database)
    : IAsyncLifetime
{
    private static readonly Guid ServerId =
        Guid.Parse("83d4df9e-7e1d-449a-b343-2b58df14a6b5");

    private static readonly DateTimeOffset OpenedAtUtc =
        new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    public Task InitializeAsync() => database.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Live_unavailable_duplicate_is_idempotent_and_enqueues_one_trigger()
    {
        await using var factory = await AlertReceiverFactory.CreateAsync(
            database.ConnectionString,
            ReceiverMode.Live);
        using var client = factory.CreateClient();
        var request = CreateUnavailable();

        using var accepted = await SendAsync(client, request);
        using var duplicate = await SendAsync(client, request);

        accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        duplicate.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var persisted = await ReadStateAsync(factory);
        persisted.Events.Should().Be(1);
        persisted.Incidents.Should().Be(1);
        persisted.Outbox.Should().Be(1);
        persisted.IncidentState.Should().Be(ReceiverIncidentState.Open);
        persisted.Actions.Should().Equal(ProviderOutboxAction.Trigger);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Reused_event_id_with_changed_payload_is_rejected_without_mutation()
    {
        await using var factory = await AlertReceiverFactory.CreateAsync(
            database.ConnectionString,
            ReceiverMode.Live);
        using var client = factory.CreateClient();
        var request = CreateUnavailable();

        using var accepted = await SendAsync(client, request);
        using var conflict = await SendAsync(
            client,
            request with { Reason = "A different timeout reason." });

        accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var persisted = await ReadStateAsync(factory);
        persisted.Events.Should().Be(1);
        persisted.Incidents.Should().Be(1);
        persisted.Outbox.Should().Be(1);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Live_unavailable_recovery_and_duplicate_persist_one_transition_each()
    {
        await using var factory = await AlertReceiverFactory.CreateAsync(
            database.ConnectionString,
            ReceiverMode.Live);
        using var client = factory.CreateClient();
        var unavailable = CreateUnavailable();
        var recovered = CreateRecovered(unavailable);

        using var opening = await SendAsync(client, unavailable);
        using var recovery = await SendAsync(client, recovered);
        using var duplicateRecovery = await SendAsync(client, recovered);

        opening.StatusCode.Should().Be(HttpStatusCode.Accepted);
        recovery.StatusCode.Should().Be(HttpStatusCode.Accepted);
        duplicateRecovery.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var persisted = await ReadStateAsync(factory);
        persisted.Events.Should().Be(2);
        persisted.Incidents.Should().Be(1);
        persisted.Outbox.Should().Be(2);
        persisted.IncidentState.Should().Be(ReceiverIncidentState.Resolved);
        persisted.Actions.Should().BeEquivalentTo(
            [ProviderOutboxAction.Trigger, ProviderOutboxAction.Resolve]);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task CatchUp_persists_history_without_provider_actions()
    {
        await using var factory = await AlertReceiverFactory.CreateAsync(
            database.ConnectionString,
            ReceiverMode.CatchUp);
        using var client = factory.CreateClient();
        var unavailable = CreateUnavailable();
        var recovered = CreateRecovered(unavailable);

        using var opening = await SendAsync(client, unavailable);
        using var recovery = await SendAsync(client, recovered);

        opening.StatusCode.Should().Be(HttpStatusCode.Accepted);
        recovery.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var persisted = await ReadStateAsync(factory);
        persisted.Events.Should().Be(2);
        persisted.IncidentState.Should().Be(ReceiverIncidentState.Resolved);
        persisted.Outbox.Should().Be(0);
        persisted.AllProviderActionsSuppressed.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Concurrent_exact_duplicates_create_one_event_and_one_provider_action()
    {
        await using var factory = await AlertReceiverFactory.CreateAsync(
            database.ConnectionString,
            ReceiverMode.Live);
        using var client = factory.CreateClient();
        var request = CreateUnavailable();

        var responseTasks = Enumerable.Range(0, 8)
            .Select(_ => SendAsync(client, request));
        var responses = await Task.WhenAll(responseTasks);

        try
        {
            responses.Count(x => x.StatusCode == HttpStatusCode.Accepted).Should().Be(1);
            responses.Count(x => x.StatusCode == HttpStatusCode.NoContent).Should().Be(7);
            var persisted = await ReadStateAsync(factory);
            persisted.Events.Should().Be(1);
            persisted.Incidents.Should().Be(1);
            persisted.Outbox.Should().Be(1);
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
    public async Task Concurrent_distinct_openings_for_one_incident_accept_one_transition()
    {
        await using var factory = await AlertReceiverFactory.CreateAsync(
            database.ConnectionString,
            ReceiverMode.Live);
        using var client = factory.CreateClient();
        var firstRequest = CreateUnavailable();
        var secondRequest = firstRequest with { EventId = Guid.NewGuid() };

        var responses = await Task.WhenAll(
            SendAsync(client, firstRequest),
            SendAsync(client, secondRequest));

        try
        {
            responses.Count(x => x.StatusCode == HttpStatusCode.Accepted).Should().Be(1);
            responses.Count(x => x.StatusCode == HttpStatusCode.Conflict).Should().Be(1);
            var persisted = await ReadStateAsync(factory);
            persisted.Events.Should().Be(1);
            persisted.Incidents.Should().Be(1);
            persisted.Outbox.Should().Be(1);
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
    public async Task Recovery_before_unavailable_is_rejected_without_persistence()
    {
        await using var factory = await AlertReceiverFactory.CreateAsync(
            database.ConnectionString,
            ReceiverMode.Live);
        using var client = factory.CreateClient();
        var unavailable = CreateUnavailable();

        using var response = await SendAsync(client, CreateRecovered(unavailable));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var persisted = await ReadStateAsync(factory);
        persisted.Events.Should().Be(0);
        persisted.Incidents.Should().Be(0);
        persisted.Outbox.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Authorization_and_idempotency_headers_are_fail_closed()
    {
        await using var factory = await AlertReceiverFactory.CreateAsync(
            database.ConnectionString,
            ReceiverMode.Live);
        using var client = factory.CreateClient();
        var request = CreateUnavailable();

        using var unauthorizedMessage = CreateMessage(request);
        unauthorizedMessage.Headers.Remove("Authorization");
        using var unauthorized = await client.SendAsync(unauthorizedMessage);

        using var mismatchedMessage = CreateMessage(request);
        mismatchedMessage.Headers.Remove("Idempotency-Key");
        mismatchedMessage.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var mismatched = await client.SendAsync(mismatchedMessage);

        unauthorized.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        mismatched.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var persisted = await ReadStateAsync(factory);
        persisted.Events.Should().Be(0);
        persisted.Incidents.Should().Be(0);
        persisted.Outbox.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Unknown_json_member_is_rejected_without_persistence()
    {
        await using var factory = await AlertReceiverFactory.CreateAsync(
            database.ConnectionString,
            ReceiverMode.Live);
        using var client = factory.CreateClient();
        var request = CreateUnavailable();
        var payload = JsonSerializer.SerializeToNode(request, JsonSerializerOptions.Web)
            ?.AsObject();
        payload.Should().NotBeNull();
        payload!["unexpected"] = JsonValue.Create("rejected");
        using var message = CreateMessage(request, JsonContent.Create(payload));

        using var response = await client.SendAsync(message);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var persisted = await ReadStateAsync(factory);
        persisted.Events.Should().Be(0);
        persisted.Incidents.Should().Be(0);
        persisted.Outbox.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Health_probes_are_secret_free_and_readiness_checks_postgresql()
    {
        await using var factory = await AlertReceiverFactory.CreateAsync(
            database.ConnectionString,
            ReceiverMode.CatchUp);
        using var client = factory.CreateClient();

        using var live = await client.GetAsync("/health/live");
        using var ready = await client.GetAsync("/health/ready");

        live.StatusCode.Should().Be(HttpStatusCode.OK);
        ready.StatusCode.Should().Be(HttpStatusCode.OK);
        var bodies = await Task.WhenAll(
            live.Content.ReadAsStringAsync(),
            ready.Content.ReadAsStringAsync());
        bodies.Should().OnlyContain(body =>
            !body.Contains(AlertReceiverFactory.Authorization, StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Accepted_event_remains_idempotent_after_receiver_restart()
    {
        var request = CreateUnavailable();

        await using (var firstFactory = await AlertReceiverFactory.CreateAsync(
            database.ConnectionString,
            ReceiverMode.Live))
        {
            using var firstClient = firstFactory.CreateClient();
            using var accepted = await SendAsync(firstClient, request);
            accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        }

        await using var restartedFactory = await AlertReceiverFactory.CreateAsync(
            database.ConnectionString,
            ReceiverMode.Live);
        using var restartedClient = restartedFactory.CreateClient();
        using var duplicate = await SendAsync(restartedClient, request);

        duplicate.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var persisted = await ReadStateAsync(restartedFactory);
        persisted.Events.Should().Be(1);
        persisted.Incidents.Should().Be(1);
        persisted.Outbox.Should().Be(1);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Provider_claim_preserves_incident_order_until_predecessor_is_processed()
    {
        await using var factory = await AlertReceiverFactory.CreateAsync(
            database.ConnectionString,
            ReceiverMode.Live);
        using var client = factory.CreateClient();
        var unavailable = CreateUnavailable();
        using var opening = await SendAsync(client, unavailable);
        using var recovery = await SendAsync(client, CreateRecovered(unavailable));
        var now = DateTimeOffset.UtcNow.AddMinutes(1);

        var first = await ClaimAsync(factory, now);
        var blocked = await ClaimAsync(factory, now);
        var processed = await MarkProcessedAsync(factory, first!, now);
        var second = await ClaimAsync(factory, now);

        first.Should().NotBeNull();
        first!.Action.Should().Be(nameof(ProviderOutboxAction.Trigger));
        blocked.Should().BeNull();
        processed.Should().BeTrue();
        second.Should().NotBeNull();
        second!.Action.Should().Be(nameof(ProviderOutboxAction.Resolve));
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Provider_retry_survives_receiver_restart_and_increments_attempt()
    {
        var unavailable = CreateUnavailable();
        ClaimedProviderOutboxMessage first;
        var now = DateTimeOffset.UtcNow.AddMinutes(1);

        await using (var factory = await AlertReceiverFactory.CreateAsync(
            database.ConnectionString,
            ReceiverMode.Live))
        {
            using var client = factory.CreateClient();
            using var opening = await SendAsync(client, unavailable);
            first = (await ClaimAsync(factory, now))!;
            await using var scope = factory.Services.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IProviderOutboxStore>();
            (await store.ScheduleRetryAsync(
                first.Id,
                first.ClaimId,
                now,
                "Synthetic retry.",
                CancellationToken.None)).Should().BeTrue();
        }

        await using var restarted = await AlertReceiverFactory.CreateAsync(
            database.ConnectionString,
            ReceiverMode.Live);
        var second = await ClaimAsync(restarted, now.AddSeconds(1));

        second.Should().NotBeNull();
        second!.SourceEventId.Should().Be(first.SourceEventId);
        second.AttemptCount.Should().Be(2);
        second.ClaimId.Should().NotBe(first.ClaimId);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Provider_dead_letter_blocks_later_action_for_same_incident()
    {
        await using var factory = await AlertReceiverFactory.CreateAsync(
            database.ConnectionString,
            ReceiverMode.Live);
        using var client = factory.CreateClient();
        var unavailable = CreateUnavailable();
        using var opening = await SendAsync(client, unavailable);
        using var recovery = await SendAsync(client, CreateRecovered(unavailable));
        var now = DateTimeOffset.UtcNow.AddMinutes(1);
        var first = (await ClaimAsync(factory, now))!;

        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderOutboxStore>();
        (await store.MarkDeadLetterAsync(
            first.Id,
            first.ClaimId,
            now,
            "Synthetic permanent failure.",
            CancellationToken.None)).Should().BeTrue();

        (await ClaimAsync(factory, now.AddMinutes(1))).Should().BeNull();
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Expired_provider_claim_is_recovered_and_can_be_reclaimed()
    {
        await using var factory = await AlertReceiverFactory.CreateAsync(
            database.ConnectionString,
            ReceiverMode.Live);
        using var client = factory.CreateClient();
        using var opening = await SendAsync(client, CreateUnavailable());
        var claimedAt = DateTimeOffset.UtcNow.AddMinutes(1);
        var first = (await ClaimAsync(factory, claimedAt))!;

        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderOutboxStore>();
        var recovery = await store.RecoverExpiredClaimsAsync(
            claimedAt.AddSeconds(1),
            claimedAt.AddMinutes(1),
            maxAttempts: 5,
            CancellationToken.None);
        var second = await ClaimAsync(factory, claimedAt.AddMinutes(1));

        recovery.RetryScheduled.Should().Be(1);
        recovery.DeadLettered.Should().Be(0);
        second.Should().NotBeNull();
        second!.Id.Should().Be(first.Id);
        second.AttemptCount.Should().Be(2);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Processed_provider_messages_are_deleted_by_bounded_retention()
    {
        await using var factory = await AlertReceiverFactory.CreateAsync(
            database.ConnectionString,
            ReceiverMode.Live);
        using var client = factory.CreateClient();
        using var opening = await SendAsync(client, CreateUnavailable());
        var claimedAt = DateTimeOffset.UtcNow.AddMinutes(1);
        var claimed = (await ClaimAsync(factory, claimedAt))!;
        var processedAt = claimedAt.AddDays(-8);
        (await MarkProcessedAsync(factory, claimed, processedAt)).Should().BeTrue();

        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderOutboxStore>();
        var deleted = await store.DeleteProcessedBatchAsync(
            claimedAt.AddDays(-7),
            batchSize: 1,
            CancellationToken.None);

        deleted.Should().Be(1);
        (await store.GetStatisticsAsync(CancellationToken.None)).PendingCount.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Concurrent_provider_claimers_lease_one_message_once()
    {
        await using var factory = await AlertReceiverFactory.CreateAsync(
            database.ConnectionString,
            ReceiverMode.Live);
        using var client = factory.CreateClient();
        using var opening = await SendAsync(client, CreateUnavailable());
        var claimedAt = DateTimeOffset.UtcNow.AddMinutes(1);

        var claims = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => ClaimAsync(factory, claimedAt)));

        claims.Count(static claim => claim is not null).Should().Be(1);
        claims.Single(static claim => claim is not null)!.AttemptCount.Should().Be(1);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Metrics_endpoint_exposes_only_bounded_receiver_dimensions()
    {
        await using var factory = await AlertReceiverFactory.CreateAsync(
            database.ConnectionString,
            ReceiverMode.CatchUp);
        using var client = factory.CreateClient();
        ReceiverMetrics.RecordAttempt("synthetic");

        var body = await client.GetStringAsync("/metrics");

        body.Should().Contain("goldsrcops_receiver_provider_delivery_attempts_total");
        body.Should().NotContain(AlertReceiverFactory.Authorization);
        body.Should().NotContain(ServerId.ToString("D"));
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Lifecycle_migration_preserves_existing_pending_provider_work()
    {
        var options = new DbContextOptionsBuilder<AlertReceiverDbContext>()
            .UseNpgsql(
                database.ConnectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    AlertReceiverDbContext.MigrationsHistoryTable,
                    AlertReceiverDbContext.Schema))
            .Options;
        await using (var dbContext = new AlertReceiverDbContext(options))
        {
            var migrator = dbContext.GetService<IMigrator>();
            await migrator.MigrateAsync("20260918180602_InitialAlertReceiver");
        }

        var eventId = Guid.NewGuid();
        var incidentId = Guid.NewGuid();
        var createdAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO receiver.incidents
                    ("Id", "ServerId", "ServerName", "State", "OpenedAtUtc",
                     "ConsecutiveFailures", "OpenReason", "LastEventId", "LastEventAtUtc", "Revision")
                VALUES
                    (@incidentId, @serverId, 'Migration fixture', 'Open', @createdAtUtc,
                     3, 'Synthetic migration fixture.', @eventId, @createdAtUtc, 1);

                INSERT INTO receiver.events
                    ("Id", "IncidentId", "EventType", "PayloadVersion", "OccurredAtUtc",
                     "ReceivedAtUtc", "PayloadSha256", "Payload", "ReceiverMode", "ProviderActionSuppressed")
                VALUES
                    (@eventId, @incidentId, 'server.unavailable.v1', 1, @createdAtUtc,
                     @createdAtUtc, @payloadSha256, '{}'::jsonb, 'Live', FALSE);

                INSERT INTO receiver.provider_outbox_messages
                    ("Id", "SourceEventId", "IncidentId", "Action", "CreatedAtUtc",
                     "Payload", "Status", "AttemptCount", "NextAttemptAtUtc")
                VALUES
                    (@messageId, @eventId, @incidentId, 'Trigger', @createdAtUtc,
                     '{}'::jsonb, 'Pending', 0, @createdAtUtc);
                """;
            command.Parameters.AddWithValue("incidentId", incidentId);
            command.Parameters.AddWithValue("serverId", ServerId);
            command.Parameters.AddWithValue("eventId", eventId);
            command.Parameters.AddWithValue("messageId", Guid.NewGuid());
            command.Parameters.AddWithValue("createdAtUtc", createdAtUtc);
            command.Parameters.AddWithValue("payloadSha256", new string('0', 64));
            await command.ExecuteNonQueryAsync();
        }

        await using var factory = await AlertReceiverFactory.CreateAsync(
            database.ConnectionString,
            ReceiverMode.Live);
        var claimed = await ClaimAsync(factory, DateTimeOffset.UtcNow.AddMinutes(1));

        claimed.Should().NotBeNull();
        claimed!.SourceEventId.Should().Be(eventId);
        claimed.IncidentId.Should().Be(incidentId);
        claimed.AttemptCount.Should().Be(1);
    }

    private static async Task<ClaimedProviderOutboxMessage?> ClaimAsync(
        AlertReceiverFactory factory,
        DateTimeOffset claimedAtUtc)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderOutboxStore>();
        return await store.ClaimNextAsync(claimedAtUtc, CancellationToken.None);
    }

    private static async Task<bool> MarkProcessedAsync(
        AlertReceiverFactory factory,
        ClaimedProviderOutboxMessage message,
        DateTimeOffset processedAtUtc)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderOutboxStore>();
        return await store.MarkProcessedAsync(
            message.Id,
            message.ClaimId,
            processedAtUtc,
            CancellationToken.None);
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

    private static AvailabilityEventRequest CreateRecovered(
        AvailabilityEventRequest unavailable)
    {
        var closedAtUtc = unavailable.OpenedAtUtc.AddMinutes(3);
        return unavailable with
        {
            EventId = Guid.NewGuid(),
            EventType = IncidentAlertEvents.ServerRecovered,
            OccurredAtUtc = closedAtUtc,
            Reason = "Server query recovered.",
            ClosedAtUtc = closedAtUtc,
            DurationSeconds = 180,
        };
    }

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        AvailabilityEventRequest request) =>
        client.SendAsync(CreateMessage(request));

    private static HttpRequestMessage CreateMessage(
        AvailabilityEventRequest request,
        HttpContent? content = null)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/availability-events")
        {
            Content = content ?? JsonContent.Create(request),
        };
        message.Headers.Add("Authorization", AlertReceiverFactory.Authorization);
        message.Headers.Add("Idempotency-Key", request.EventId.ToString("D"));
        return message;
    }

    private static Task<PersistedState> ReadStateAsync(AlertReceiverFactory factory) =>
        factory.ExecuteDbContextAsync(async dbContext =>
        {
            var incident = await dbContext.Incidents
                .AsNoTracking()
                .SingleOrDefaultAsync();
            var events = await dbContext.ReceivedEvents
                .AsNoTracking()
                .ToListAsync();
            var actions = await dbContext.ProviderOutboxMessages
                .AsNoTracking()
                .OrderBy(x => x.CreatedAtUtc)
                .ThenBy(x => x.Id)
                .Select(x => x.Action)
                .ToListAsync();

            return new PersistedState(
                events.Count,
                incident is null ? 0 : 1,
                actions.Count,
                incident?.State,
                actions,
                events.All(x => x.ProviderActionSuppressed));
        });

    private sealed record PersistedState(
        int Events,
        int Incidents,
        int Outbox,
        ReceiverIncidentState? IncidentState,
        IReadOnlyList<ProviderOutboxAction> Actions,
        bool AllProviderActionsSuppressed);
}
