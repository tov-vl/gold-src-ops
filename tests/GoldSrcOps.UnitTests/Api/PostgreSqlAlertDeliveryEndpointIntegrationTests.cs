using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using GoldSrcOps.Application.Alerts;
using GoldSrcOps.Contracts.Alerts;
using GoldSrcOps.Infrastructure.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GoldSrcOps.UnitTests.Api;

public sealed class PostgreSqlAlertDeliveryEndpointIntegrationTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

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
