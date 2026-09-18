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
using Moq;

namespace GoldSrcOps.UnitTests.Api;

public sealed class PostgreSqlAlertDeliveryEndpointIntegrationTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task GetStatus_returns_configuration_and_aggregate_queue_state()
    {
        var observedAtUtc = new DateTimeOffset(2026, 9, 17, 18, 22, 0, TimeSpan.Zero);
        var clock = new Mock<IClock>(MockBehavior.Strict);
        clock.SetupGet(static candidate => candidate.UtcNow).Returns(observedAtUtc);
        await using var factory = await PostgreSqlGoldSrcOpsApiFactory.CreateAsync(
            services =>
            {
                services.RemoveAll<AlertDeliveryStatusSettings>();
                services.RemoveAll<IClock>();
                services.AddSingleton(new AlertDeliveryStatusSettings(IsEnabled: true));
                services.AddSingleton(clock.Object);
            },
            TestApiPrincipal.Reader());
        using var client = factory.CreateClient();
        var deadLetter = CreateMessage(
            Guid.NewGuid(),
            Guid.NewGuid(),
            observedAtUtc.AddMinutes(-30),
            IncidentAlertEvents.ServerUnavailable,
            "{\"state\":\"dead-letter\"}");
        var processing = CreateMessage(
            Guid.NewGuid(),
            Guid.NewGuid(),
            observedAtUtc.AddMinutes(-20),
            IncidentAlertEvents.ServerUnavailable,
            "{\"state\":\"processing\"}");
        var pending = CreateMessage(
            Guid.NewGuid(),
            Guid.NewGuid(),
            observedAtUtc.AddMinutes(-10),
            IncidentAlertEvents.ServerRecovered,
            "{\"state\":\"pending\"}");
        await SeedAsync(factory, deadLetter, processing, pending);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
            var deadLetterClaim = await store.ClaimNextPendingAsync(
                observedAtUtc,
                CancellationToken.None);
            deadLetterClaim.Should().NotBeNull();
            deadLetterClaim!.Id.Should().Be(deadLetter.Id);
            (await store.MarkDeadLetterAsync(
                deadLetter.Id,
                deadLetterClaim.ClaimId,
                observedAtUtc.AddMinutes(-25),
                "delivery exhausted",
                CancellationToken.None)).Should().BeTrue();

            var processingClaim = await store.ClaimNextPendingAsync(
                observedAtUtc,
                CancellationToken.None);
            processingClaim.Should().NotBeNull();
            processingClaim!.Id.Should().Be(processing.Id);
        }

        var response = await client.GetAsync("/api/alert-delivery/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var status = await response.Content.ReadFromJsonAsync<AlertDeliveryStatusResponse>();
        status.Should().Be(new AlertDeliveryStatusResponse(
            IsEnabled: true,
            PendingCount: 1,
            ProcessingCount: 1,
            DeadLetterCount: 1,
            pending.OccurredAtUtc,
            observedAtUtc));
        clock.VerifyAll();
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task ListPendingDeliveries_uses_a_stable_cursor_and_returns_only_safe_triage_fields()
    {
        await using var factory = await PostgreSqlGoldSrcOpsApiFactory.CreateAsync(
            principal: TestApiPrincipal.Reader());
        using var client = factory.CreateClient();
        var occurredAtUtc = new DateTimeOffset(2026, 9, 18, 8, 0, 0, TimeSpan.Zero);
        var firstEventId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var secondEventId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var missingEventId = Guid.Parse("00000000-0000-0000-0000-000000000003");

        var links = await factory.ExecuteDbContextAsync(async dbContext =>
        {
            var openServer = new Server(
                "Alpha pending server",
                GameServerKind.GoldSrc,
                new ServerEndpoint("alpha.example.test", queryPort: 27015, rconPort: null),
                pollIntervalSeconds: 30,
                notes: null,
                occurredAtUtc.AddHours(-1));
            var recoveredServer = new Server(
                "Bravo recovered server",
                GameServerKind.GoldSrc,
                new ServerEndpoint("bravo.example.test", queryPort: 27015, rconPort: null),
                pollIntervalSeconds: 30,
                notes: null,
                occurredAtUtc.AddHours(-1));
            var openIncident = AvailabilityIncident.Open(
                openServer.Id,
                occurredAtUtc.AddMinutes(-5),
                "server query failed",
                consecutiveFailures: 3);
            var recoveredIncident = AvailabilityIncident.Open(
                recoveredServer.Id,
                occurredAtUtc.AddMinutes(-10),
                "server query failed",
                consecutiveFailures: 3);
            recoveredIncident.Close(occurredAtUtc.AddMinutes(-4), "server query recovered");

            dbContext.Servers.AddRange(openServer, recoveredServer);
            dbContext.AvailabilityIncidents.AddRange(openIncident, recoveredIncident);
            dbContext.OutboxMessages.AddRange(
                CreateMessage(
                    firstEventId,
                    openIncident.Id,
                    occurredAtUtc,
                    IncidentAlertEvents.ServerUnavailable,
                    "{\"secret\":\"first-payload-must-not-render\"}"),
                CreateMessage(
                    secondEventId,
                    recoveredIncident.Id,
                    occurredAtUtc.AddMinutes(1),
                    IncidentAlertEvents.ServerRecovered,
                    "{\"secret\":\"second-payload-must-not-render\"}"),
                CreateMessage(
                    missingEventId,
                    Guid.NewGuid(),
                    occurredAtUtc.AddMinutes(2),
                    IncidentAlertEvents.ServerUnavailable,
                    "{\"secret\":\"missing-payload-must-not-render\"}"));
            await dbContext.SaveChangesAsync();

            return new
            {
                OpenIncidentId = openIncident.Id,
                OpenServerId = openServer.Id,
                RecoveredIncidentId = recoveredIncident.Id,
                RecoveredServerId = recoveredServer.Id
            };
        });

        var firstResponse = await client.GetAsync("/api/alert-delivery/pending?limit=2");

        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstJson = await firstResponse.Content.ReadAsStringAsync();
        var firstPage = Deserialize<PendingDeliveryListResponse>(firstJson);
        firstPage.Limit.Should().Be(2);
        firstPage.Items.Select(static item => item.EventId).Should().Equal(firstEventId, secondEventId);
        firstPage.NextCursor.Should().NotBeNullOrWhiteSpace();
        firstPage.Items[0].Should().Be(new PendingDeliveryListItemResponse(
            firstEventId,
            IncidentAlertEvents.ServerUnavailable,
            occurredAtUtc,
            AttemptCount: 0,
            NextAttemptAtUtc: occurredAtUtc,
            links.OpenIncidentId,
            "Open",
            links.OpenServerId,
            "Alpha pending server"));
        firstPage.Items[1].IncidentStatus.Should().Be("Recovered");
        firstPage.Items[1].IncidentId.Should().Be(links.RecoveredIncidentId);
        firstPage.Items[1].ServerId.Should().Be(links.RecoveredServerId);
        using (var document = JsonDocument.Parse(firstJson))
        {
            var firstItem = document.RootElement.GetProperty("items")[0];
            firstItem.TryGetProperty("payload", out _).Should().BeFalse();
            firstItem.TryGetProperty("lastError", out _).Should().BeFalse();
            firstItem.TryGetProperty("claimId", out _).Should().BeFalse();
            firstJson.Should().NotContain("example.test");
            firstJson.Should().NotContain("payload-must-not-render");
        }

        var secondResponse = await client.GetAsync(
            $"/api/alert-delivery/pending?limit=2&cursor={Uri.EscapeDataString(firstPage.NextCursor!)}");
        var secondPage = await secondResponse.Content.ReadFromJsonAsync<PendingDeliveryListResponse>();

        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        secondPage.Should().NotBeNull();
        secondPage!.Items.Should().ContainSingle();
        secondPage.Items[0].EventId.Should().Be(missingEventId);
        secondPage.Items[0].IncidentId.Should().BeNull();
        secondPage.Items[0].IncidentStatus.Should().Be("Missing");
        secondPage.Items[0].ServerId.Should().BeNull();
        secondPage.Items[0].ServerName.Should().BeNull();
        secondPage.NextCursor.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task ListDeadLetters_uses_a_stable_cursor_and_omits_payloads()
    {
        await using var factory = await PostgreSqlGoldSrcOpsApiFactory.CreateAsync(
            principal: TestApiPrincipal.Reader());
        using var client = factory.CreateClient();
        var occurredAtUtc = new DateTimeOffset(2026, 8, 26, 10, 0, 0, TimeSpan.Zero);
        var commonDeadLetteredAtUtc = occurredAtUtc.AddHours(2);
        var first = CreateMessage(
            "00000000-0000-0000-0000-000000000004",
            occurredAtUtc,
            payload: "{\"sequence\":4}");
        var second = CreateMessage(
            "00000000-0000-0000-0000-000000000003",
            occurredAtUtc,
            payload: "{\"sequence\":3}");
        var third = CreateMessage(
            "00000000-0000-0000-0000-000000000002",
            occurredAtUtc.AddHours(-1),
            payload: "{\"sequence\":2}");
        var legacy = CreateMessage(
            "00000000-0000-0000-0000-000000000001",
            occurredAtUtc.AddHours(3),
            payload: "{\"sequence\":1}");
        await SeedDeadLetterAsync(factory, first, commonDeadLetteredAtUtc, "first failure");
        await SeedDeadLetterAsync(factory, second, commonDeadLetteredAtUtc, "second failure");
        await SeedDeadLetterAsync(factory, third, commonDeadLetteredAtUtc.AddHours(-1), "third failure");
        await SeedDeadLetterAsync(factory, legacy, deadLetteredAtUtc: null, "legacy failure");

        var firstResponse = await client.GetAsync("/api/alert-delivery/dead-letters?limit=2");

        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstJson = await firstResponse.Content.ReadAsStringAsync();
        var firstPage = Deserialize<DeadLetterListResponse>(firstJson);
        firstPage.Limit.Should().Be(2);
        firstPage.Items.Select(static item => item.EventId).Should().Equal(first.Id, second.Id);
        firstPage.NextCursor.Should().NotBeNullOrWhiteSpace();
        using (var document = JsonDocument.Parse(firstJson))
        {
            document.RootElement
                .GetProperty("items")[0]
                .TryGetProperty("payload", out _)
                .Should()
                .BeFalse();
        }

        var insertedAfterFirstPage = CreateMessage(
            "00000000-0000-0000-0000-000000000005",
            occurredAtUtc.AddHours(4),
            payload: "{\"sequence\":5}");
        await SeedDeadLetterAsync(
            factory,
            insertedAfterFirstPage,
            commonDeadLetteredAtUtc.AddHours(2),
            "new failure");

        var secondResponse = await client.GetAsync(
            $"/api/alert-delivery/dead-letters?limit=2&cursor={Uri.EscapeDataString(firstPage.NextCursor!)}");

        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondPage = await secondResponse.Content.ReadFromJsonAsync<DeadLetterListResponse>();
        secondPage.Should().NotBeNull();
        secondPage!.Items.Select(static item => item.EventId).Should().Equal(third.Id, legacy.Id);
        secondPage.Items.Should().OnlyContain(static item => item.EventId != Guid.Empty);
        secondPage.Items.Select(static item => item.EventId).Should().NotContain(insertedAfterFirstPage.Id);
        secondPage.NextCursor.Should().BeNull();

        var freshResponse = await client.GetAsync("/api/alert-delivery/dead-letters?limit=1");
        var freshPage = await freshResponse.Content.ReadFromJsonAsync<DeadLetterListResponse>();
        freshPage.Should().NotBeNull();
        freshPage!.Items.Should().ContainSingle().Which.EventId.Should().Be(insertedAfterFirstPage.Id);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task GetDeadLetter_returns_the_exact_payload_and_latest_newer_event_warning()
    {
        await using var factory = await PostgreSqlGoldSrcOpsApiFactory.CreateAsync(
            principal: TestApiPrincipal.Reader());
        using var client = factory.CreateClient();
        var incidentId = Guid.NewGuid();
        var occurredAtUtc = new DateTimeOffset(2026, 8, 26, 11, 0, 0, TimeSpan.Zero);
        var deadLetteredAtUtc = occurredAtUtc.AddMinutes(10);
        var payload = JsonSerializer.Serialize(
            new
            {
                eventId = Guid.NewGuid(),
                server = new { name = "Dust2 Public" }
            },
            SerializerOptions);
        var target = CreateMessage(
            Guid.NewGuid(),
            incidentId,
            occurredAtUtc,
            IncidentAlertEvents.ServerUnavailable,
            payload);
        await SeedDeadLetterAsync(
            factory,
            target,
            deadLetteredAtUtc,
            "permanent HTTP 400 response");
        var newer = CreateMessage(
            Guid.NewGuid(),
            incidentId,
            occurredAtUtc.AddMinutes(5),
            IncidentAlertEvents.ServerRecovered,
            "{\"recovered\":true}");
        await SeedAsync(factory, newer);

        var response = await client.GetAsync($"/api/alert-delivery/dead-letters/{target.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var details = await response.Content.ReadFromJsonAsync<DeadLetterDetailResponse>();
        details.Should().NotBeNull();
        details!.EventId.Should().Be(target.Id);
        details.Payload.GetProperty("server").GetProperty("name").GetString().Should().Be("Dust2 Public");
        details.AttemptCount.Should().Be(1);
        details.ReplayCount.Should().Be(0);
        details.DeadLetteredAtUtc.Should().Be(deadLetteredAtUtc);
        details.LastError.Should().Be("permanent HTTP 400 response");
        details.HasNewerEvent.Should().BeTrue();
        details.NewerEventId.Should().Be(newer.Id);
        details.NewerEventStatus.Should().Be("Pending");
        details.LatestKnownOccurredAtUtc.Should().Be(newer.OccurredAtUtc);

        var nonDeadLetterResponse = await client.GetAsync(
            $"/api/alert-delivery/dead-letters/{newer.Id}");
        nonDeadLetterResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task ListDeadLetters_rejects_invalid_cursor_and_limit_values()
    {
        await using var factory = await PostgreSqlGoldSrcOpsApiFactory.CreateAsync(
            principal: TestApiPrincipal.Reader());
        using var client = factory.CreateClient();

        var responses = await Task.WhenAll(
            client.GetAsync("/api/alert-delivery/dead-letters?cursor=not-a-cursor"),
            client.GetAsync("/api/alert-delivery/dead-letters?limit=0"),
            client.GetAsync($"/api/alert-delivery/dead-letters?limit={AlertDeliveryReadService.MaxDeadLetterLimit + 1}"));

        responses.Should().OnlyContain(static response => response.StatusCode == HttpStatusCode.BadRequest);
        var cursorProblem = await responses[0].Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(cursorProblem);
        document.RootElement.GetProperty("errors").TryGetProperty("cursor", out _).Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task ListPendingDeliveries_rejects_invalid_cursor_and_limit_values()
    {
        await using var factory = await PostgreSqlGoldSrcOpsApiFactory.CreateAsync(
            principal: TestApiPrincipal.Reader());
        using var client = factory.CreateClient();

        var responses = await Task.WhenAll(
            client.GetAsync("/api/alert-delivery/pending?cursor=not-a-cursor"),
            client.GetAsync("/api/alert-delivery/pending?limit=0"),
            client.GetAsync(
                $"/api/alert-delivery/pending?limit={AlertDeliveryReadService.MaxPendingDeliveryLimit + 1}"));

        responses.Should().OnlyContain(static response => response.StatusCode == HttpStatusCode.BadRequest);
        var cursorProblem = await responses[0].Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(cursorProblem);
        document.RootElement.GetProperty("errors").TryGetProperty("cursor", out _).Should().BeTrue();
    }

    private static OutboxMessage CreateMessage(
        string eventId,
        DateTimeOffset occurredAtUtc,
        string payload) =>
        CreateMessage(
            Guid.Parse(eventId),
            Guid.NewGuid(),
            occurredAtUtc,
            IncidentAlertEvents.ServerUnavailable,
            payload);

    private static OutboxMessage CreateMessage(
        Guid eventId,
        Guid incidentId,
        DateTimeOffset occurredAtUtc,
        string eventType,
        string payload) =>
        new(
            eventId,
            eventType,
            payloadVersion: 1,
            IncidentAlertEvents.AggregateType,
            incidentId,
            occurredAtUtc,
            payload);

    private static async Task SeedDeadLetterAsync(
        PostgreSqlGoldSrcOpsApiFactory factory,
        OutboxMessage message,
        DateTimeOffset? deadLetteredAtUtc,
        string lastError)
    {
        await SeedAsync(factory, message);
        var transitionAtUtc = deadLetteredAtUtc ?? message.OccurredAtUtc.AddMinutes(1);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
            var claim = await store.ClaimNextPendingAsync(transitionAtUtc, CancellationToken.None);
            claim.Should().NotBeNull();
            claim!.Id.Should().Be(message.Id);
            (await store.MarkDeadLetterAsync(
                message.Id,
                claim.ClaimId,
                transitionAtUtc,
                lastError,
                CancellationToken.None)).Should().BeTrue();
        }

        if (deadLetteredAtUtc is null)
        {
            await factory.ExecuteDbContextAsync(async dbContext =>
                await dbContext.Database.ExecuteSqlInterpolatedAsync($$"""
                    UPDATE goldsrcops.outbox_messages
                    SET "DeadLetteredAtUtc" = NULL
                    WHERE "Id" = {{message.Id}};
                    """));
        }
    }

    private static async Task SeedAsync(
        PostgreSqlGoldSrcOpsApiFactory factory,
        params OutboxMessage[] messages)
    {
        await factory.ExecuteDbContextAsync(async dbContext =>
        {
            dbContext.OutboxMessages.AddRange(messages);
            await dbContext.SaveChangesAsync();
        });
    }

    private static T Deserialize<T>(string json)
    {
        return JsonSerializer.Deserialize<T>(json, SerializerOptions)!;
    }
}
