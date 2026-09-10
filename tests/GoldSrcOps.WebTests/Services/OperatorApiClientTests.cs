using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using GoldSrcOps.Contracts.Alerts;
using GoldSrcOps.Contracts.Commands;
using GoldSrcOps.Contracts.Credentials;
using GoldSrcOps.Contracts.Servers;
using GoldSrcOps.Web.Security;
using GoldSrcOps.Web.Services;

namespace GoldSrcOps.WebTests.Services;

public sealed class OperatorApiClientTests
{
    [Fact]
    public async Task RegisterServerAsync_posts_paused_contract_and_idempotency_key()
    {
        var requestId = Guid.Parse("a195195d-3ad8-49d1-9e5a-dd70a6957c90");
        var server = new ServerResponse(
            Guid.Parse("5edc0c9a-41f7-42b0-811c-93dc0eea98e7"),
            1,
            "Public server",
            "GoldSrc",
            "game.example.test",
            27015,
            27016,
            false,
            60,
            "Paused registration",
            new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero));
        var capture = new RegistrationCaptureHandler(HttpStatusCode.Created, server);
        using var httpClient = CreateHttpClient(capture);
        var client = new OperatorApiClient(httpClient);
        var draft = new OperatorServerRegistrationDraft(
            requestId,
            server.Name,
            server.Host,
            server.QueryPort,
            server.RconPort,
            server.PollIntervalSeconds,
            server.Notes);

        var result = await client.RegisterServerAsync(draft);

        result.Should().Be(new OperatorServerRegistrationResult(
            OperatorServerRegistrationResultKind.Created,
            server));
        capture.Method.Should().Be(HttpMethod.Post);
        capture.RequestUri.Should().Be(new Uri("https://api.example.test/api/servers"));
        capture.IdempotencyKey.Should().Be(requestId.ToString("D"));
        capture.Request.Should().Be(new RegisterServerRequest(
            server.Name,
            server.Host,
            server.QueryPort,
            server.RconPort,
            server.PollIntervalSeconds,
            server.Notes,
            IsEnabled: false));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, (int)OperatorServerRegistrationResultKind.Idempotent)]
    [InlineData(HttpStatusCode.Conflict, (int)OperatorServerRegistrationResultKind.Conflict)]
    [InlineData(HttpStatusCode.BadRequest, (int)OperatorServerRegistrationResultKind.Rejected)]
    public async Task RegisterServerAsync_maps_expected_results(
        HttpStatusCode statusCode,
        int expected)
    {
        var responseServer = statusCode == HttpStatusCode.OK
            ? new ServerResponse(
                Guid.NewGuid(),
                1,
                "Server",
                "GoldSrc",
                "game.example.test",
                27015,
                null,
                false,
                60,
                null,
                DateTimeOffset.UtcNow)
            : null;
        var capture = new RegistrationCaptureHandler(statusCode, responseServer);
        using var httpClient = CreateHttpClient(capture);
        var client = new OperatorApiClient(httpClient);

        var result = await client.RegisterServerAsync(new OperatorServerRegistrationDraft(
            Guid.NewGuid(),
            "Server",
            "game.example.test",
            27015,
            null,
            60,
            null));

        result.Kind.Should().Be((OperatorServerRegistrationResultKind)expected);
        result.Server.Should().Be(responseServer);
    }

    [Fact]
    public async Task QueueSayAsync_posts_only_the_say_contract()
    {
        var serverId = Guid.Parse("755b406e-f627-4c79-85c0-521e4cebe9b0");
        const string message = "Maintenance begins in five minutes";
        var capture = new CaptureHandler(HttpStatusCode.Created);
        using var httpClient = CreateHttpClient(capture);
        var client = new OperatorApiClient(httpClient);

        var result = await client.QueueSayAsync(serverId, message);

        result.Should().Be(OperatorCommandQueueResult.Queued);
        capture.Method.Should().Be(HttpMethod.Post);
        capture.RequestUri.Should().Be(
            new Uri($"https://api.example.test/api/servers/{serverId:D}/commands/say"));
        capture.Request.Should().Be(new SayCommandRequest(message));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, (int)OperatorCommandQueueResult.ServerNotFound)]
    [InlineData(HttpStatusCode.Conflict, (int)OperatorCommandQueueResult.MissingRconCredential)]
    [InlineData(HttpStatusCode.BadRequest, (int)OperatorCommandQueueResult.Rejected)]
    public async Task QueueSayAsync_maps_expected_rejections(
        HttpStatusCode statusCode,
        int expected)
    {
        var capture = new CaptureHandler(statusCode);
        using var httpClient = CreateHttpClient(capture);
        var client = new OperatorApiClient(httpClient);

        var result = await client.QueueSayAsync(Guid.NewGuid(), "Message");

        result.Should().Be((OperatorCommandQueueResult)expected);
    }

    [Fact]
    public async Task QueueSayAsync_rejects_an_unexpected_status()
    {
        var capture = new CaptureHandler(HttpStatusCode.OK);
        using var httpClient = CreateHttpClient(capture);
        var client = new OperatorApiClient(httpClient);

        var action = () => client.QueueSayAsync(Guid.NewGuid(), "Message");

        var exception = await action.Should().ThrowAsync<HttpRequestException>();
        exception.Which.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task QueueMapChangeAsync_posts_only_the_change_map_contract()
    {
        var serverId = Guid.Parse("a149e5c1-c20f-427f-80f5-f7a41fc19819");
        const string map = "de_dust2";
        var capture = new MapChangeCaptureHandler(HttpStatusCode.Created);
        using var httpClient = CreateHttpClient(capture);
        var client = new OperatorApiClient(httpClient);

        var result = await client.QueueMapChangeAsync(serverId, map);

        result.Should().Be(OperatorCommandQueueResult.Queued);
        capture.Method.Should().Be(HttpMethod.Post);
        capture.RequestUri.Should().Be(new Uri(
            $"https://api.example.test/api/servers/{serverId:D}/commands/change-map"));
        capture.Request.Should().Be(new ChangeMapCommandRequest(map));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, (int)OperatorCommandQueueResult.ServerNotFound)]
    [InlineData(HttpStatusCode.Conflict, (int)OperatorCommandQueueResult.MissingRconCredential)]
    [InlineData(HttpStatusCode.BadRequest, (int)OperatorCommandQueueResult.Rejected)]
    public async Task QueueMapChangeAsync_maps_expected_rejections(
        HttpStatusCode statusCode,
        int expected)
    {
        var capture = new MapChangeCaptureHandler(statusCode);
        using var httpClient = CreateHttpClient(capture);
        var client = new OperatorApiClient(httpClient);

        var result = await client.QueueMapChangeAsync(Guid.NewGuid(), "de_dust2");

        result.Should().Be((OperatorCommandQueueResult)expected);
    }

    [Fact]
    public async Task QueueMapChangeAsync_rejects_an_unexpected_status()
    {
        var capture = new MapChangeCaptureHandler(HttpStatusCode.OK);
        using var httpClient = CreateHttpClient(capture);
        var client = new OperatorApiClient(httpClient);

        var action = () => client.QueueMapChangeAsync(Guid.NewGuid(), "de_dust2");

        var exception = await action.Should().ThrowAsync<HttpRequestException>();
        exception.Which.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task QueueRestartAsync_posts_only_the_restart_action()
    {
        var serverId = Guid.Parse("7ffecba6-623a-4e1b-897a-bf85cad55079");
        var capture = new LifecycleCaptureHandler(HttpStatusCode.Created);
        using var httpClient = CreateHttpClient(capture);
        var client = new OperatorApiClient(httpClient);

        var result = await client.QueueRestartAsync(serverId);

        result.Should().Be(OperatorCommandQueueResult.Queued);
        capture.Method.Should().Be(HttpMethod.Post);
        capture.RequestUri.Should().Be(
            new Uri($"https://api.example.test/api/servers/{serverId:D}/commands/restart"));
        capture.HasContent.Should().BeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, (int)OperatorCommandQueueResult.ServerNotFound)]
    [InlineData(HttpStatusCode.Conflict, (int)OperatorCommandQueueResult.MissingRconCredential)]
    [InlineData(HttpStatusCode.BadRequest, (int)OperatorCommandQueueResult.Rejected)]
    public async Task QueueRestartAsync_maps_expected_rejections(
        HttpStatusCode statusCode,
        int expected)
    {
        var capture = new LifecycleCaptureHandler(statusCode);
        using var httpClient = CreateHttpClient(capture);
        var client = new OperatorApiClient(httpClient);

        var result = await client.QueueRestartAsync(Guid.NewGuid());

        result.Should().Be((OperatorCommandQueueResult)expected);
    }

    [Fact]
    public async Task QueueRestartAsync_rejects_an_unexpected_status()
    {
        var capture = new LifecycleCaptureHandler(HttpStatusCode.OK);
        using var httpClient = CreateHttpClient(capture);
        var client = new OperatorApiClient(httpClient);

        var action = () => client.QueueRestartAsync(Guid.NewGuid());

        var exception = await action.Should().ThrowAsync<HttpRequestException>();
        exception.Which.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(true, "enable")]
    [InlineData(false, "disable")]
    public async Task SetMonitoringEnabledAsync_posts_only_the_requested_lifecycle_action(
        bool enabled,
        string actionSegment)
    {
        var serverId = Guid.Parse("3bb3ed44-2e10-442c-b1e1-62bb3f4a2b35");
        var capture = new LifecycleCaptureHandler(HttpStatusCode.OK);
        using var httpClient = CreateHttpClient(capture);
        var client = new OperatorApiClient(httpClient);

        var result = await client.SetMonitoringEnabledAsync(serverId, enabled);

        result.Should().Be(OperatorMonitoringUpdateResult.Updated);
        capture.Method.Should().Be(HttpMethod.Post);
        capture.RequestUri.Should().Be(
            new Uri($"https://api.example.test/api/servers/{serverId:D}/{actionSegment}"));
        capture.HasContent.Should().BeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, (int)OperatorMonitoringUpdateResult.ServerNotFound)]
    [InlineData(HttpStatusCode.Conflict, (int)OperatorMonitoringUpdateResult.Conflict)]
    public async Task SetMonitoringEnabledAsync_maps_expected_rejections(
        HttpStatusCode statusCode,
        int expected)
    {
        var capture = new LifecycleCaptureHandler(statusCode);
        using var httpClient = CreateHttpClient(capture);
        var client = new OperatorApiClient(httpClient);

        var result = await client.SetMonitoringEnabledAsync(Guid.NewGuid(), enabled: true);

        result.Should().Be((OperatorMonitoringUpdateResult)expected);
    }

    [Fact]
    public async Task SetMonitoringEnabledAsync_rejects_an_unexpected_status()
    {
        var capture = new LifecycleCaptureHandler(HttpStatusCode.Accepted);
        using var httpClient = CreateHttpClient(capture);
        var client = new OperatorApiClient(httpClient);

        var action = () => client.SetMonitoringEnabledAsync(Guid.NewGuid(), enabled: false);

        var exception = await action.Should().ThrowAsync<HttpRequestException>();
        exception.Which.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task UpdateServerAsync_patches_the_reviewed_revision_and_non_secret_fields()
    {
        var serverId = Guid.Parse("c1f3f963-8b8f-4ffd-a1f9-277d3d117ec9");
        var draft = new OperatorServerUpdateDraft(
            serverId,
            ExpectedRevision: 7,
            "Updated server",
            "game-updated.example.test",
            QueryPort: 27016,
            RconPort: 27017,
            PollIntervalSeconds: 45,
            Notes: "Reviewed update");
        var updated = new ServerResponse(
            serverId,
            8,
            draft.Name,
            "GoldSrc",
            draft.Host,
            draft.QueryPort,
            draft.RconPort,
            false,
            draft.PollIntervalSeconds,
            draft.Notes,
            new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero));
        var capture = new UpdateCaptureHandler(HttpStatusCode.OK, updated);
        using var httpClient = CreateHttpClient(capture);
        var client = new OperatorApiClient(httpClient);

        var result = await client.UpdateServerAsync(draft);

        result.Should().Be(new OperatorServerUpdateResult(
            OperatorServerUpdateResultKind.Updated,
            updated));
        capture.Method.Should().Be(HttpMethod.Patch);
        capture.RequestUri.Should().Be(
            new Uri($"https://api.example.test/api/servers/{serverId:D}"));
        capture.Request.Should().Be(new UpdateServerRequest(
            draft.ExpectedRevision,
            draft.Name,
            draft.Host,
            draft.QueryPort,
            draft.RconPort,
            draft.PollIntervalSeconds,
            draft.Notes));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, (int)OperatorServerUpdateResultKind.ServerNotFound)]
    [InlineData(HttpStatusCode.Conflict, (int)OperatorServerUpdateResultKind.Conflict)]
    [InlineData(HttpStatusCode.BadRequest, (int)OperatorServerUpdateResultKind.Rejected)]
    public async Task UpdateServerAsync_maps_expected_rejections(
        HttpStatusCode statusCode,
        int expected)
    {
        var capture = new UpdateCaptureHandler(statusCode, responseServer: null);
        using var httpClient = CreateHttpClient(capture);
        var client = new OperatorApiClient(httpClient);

        var result = await client.UpdateServerAsync(CreateUpdateDraft());

        result.Kind.Should().Be((OperatorServerUpdateResultKind)expected);
        result.Server.Should().BeNull();
    }

    [Fact]
    public async Task UpdateServerAsync_rejects_an_unexpected_status()
    {
        var capture = new UpdateCaptureHandler(HttpStatusCode.Accepted, responseServer: null);
        using var httpClient = CreateHttpClient(capture);
        var client = new OperatorApiClient(httpClient);

        var action = () => client.UpdateServerAsync(CreateUpdateDraft());

        var exception = await action.Should().ThrowAsync<HttpRequestException>();
        exception.Which.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task SetRconCredentialAsync_puts_only_alias_and_expected_revisions()
    {
        var draft = new OperatorRconCredentialDraft(
            Guid.Parse("8d9bf6ce-0b6b-4863-b4cd-a8d937f03629"),
            ExpectedServerRevision: 7,
            ExpectedCredentialRevision: 3,
            SecretAlias: "primary_server");
        var updated = new ServerCredentialResponse(
            Guid.Parse("43df3ae5-c7d5-46f7-8793-43d1f6bd26ca"),
            draft.ServerId,
            4,
            "RconPassword",
            true,
            new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero));
        var capture = new CredentialCaptureHandler(HttpStatusCode.OK, updated, problemCode: null);
        using var httpClient = CreateHttpClient(capture);
        var client = new OperatorApiClient(httpClient);

        var result = await client.SetRconCredentialAsync(draft);

        result.Should().Be(new OperatorRconCredentialUpdateResult(
            OperatorRconCredentialUpdateResultKind.Updated,
            updated));
        capture.Method.Should().Be(HttpMethod.Put);
        capture.RequestUri.Should().Be(new Uri(
            $"https://api.example.test/api/servers/{draft.ServerId:D}/credentials/rcon"));
        capture.Request.Should().Be(new SetRconCredentialRequest(
            draft.ExpectedServerRevision,
            draft.ExpectedCredentialRevision,
            draft.SecretAlias));
    }

    [Theory]
    [InlineData("rcon_credential.monitoring_must_be_paused", (int)OperatorRconCredentialUpdateResultKind.MonitoringEnabled)]
    [InlineData("rcon_credential.commands_in_progress", (int)OperatorRconCredentialUpdateResultKind.CommandsInProgress)]
    [InlineData("rcon_credential.revision_conflict", (int)OperatorRconCredentialUpdateResultKind.Conflict)]
    [InlineData(null, (int)OperatorRconCredentialUpdateResultKind.Conflict)]
    public async Task SetRconCredentialAsync_maps_conflict_code(
        string? problemCode,
        int expected)
    {
        var capture = new CredentialCaptureHandler(
            HttpStatusCode.Conflict,
            credential: null,
            problemCode);
        using var httpClient = CreateHttpClient(capture);
        var client = new OperatorApiClient(httpClient);

        var result = await client.SetRconCredentialAsync(CreateCredentialDraft());

        result.Kind.Should().Be((OperatorRconCredentialUpdateResultKind)expected);
        result.Credential.Should().BeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, (int)OperatorRconCredentialUpdateResultKind.ServerNotFound)]
    [InlineData(HttpStatusCode.BadRequest, (int)OperatorRconCredentialUpdateResultKind.Rejected)]
    public async Task SetRconCredentialAsync_maps_expected_rejections(
        HttpStatusCode statusCode,
        int expected)
    {
        var capture = new CredentialCaptureHandler(statusCode, credential: null, problemCode: null);
        using var httpClient = CreateHttpClient(capture);
        var client = new OperatorApiClient(httpClient);

        var result = await client.SetRconCredentialAsync(CreateCredentialDraft());

        result.Kind.Should().Be((OperatorRconCredentialUpdateResultKind)expected);
    }

    [Fact]
    public async Task SetRconCredentialAsync_rejects_invalid_success_payload()
    {
        var draft = CreateCredentialDraft();
        var invalid = new ServerCredentialResponse(
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            "RconPassword",
            true,
            DateTimeOffset.UtcNow,
            null);
        var capture = new CredentialCaptureHandler(HttpStatusCode.OK, invalid, problemCode: null);
        using var httpClient = CreateHttpClient(capture);
        var client = new OperatorApiClient(httpClient);

        var action = () => client.SetRconCredentialAsync(draft);

        await action.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task ReplayDeadLetterAsync_posts_reason_and_idempotency_key()
    {
        var eventId = Guid.Parse("419bb150-112f-4f58-a6bf-162ca41a0895");
        var requestId = Guid.Parse("418e3f0f-b52f-494f-99de-06b2b37e43ad");
        const string reason = "Receiver health was verified";
        var capture = new ReplayCaptureHandler(HttpStatusCode.Accepted);
        using var httpClient = CreateHttpClient(capture);
        var client = new OperatorApiClient(httpClient);

        var result = await client.ReplayDeadLetterAsync(eventId, requestId, reason);

        result.Should().Be(OperatorReplayResult.Accepted);
        capture.Method.Should().Be(HttpMethod.Post);
        capture.RequestUri.Should().Be(
            new Uri($"https://api.example.test/api/alert-delivery/dead-letters/{eventId:D}/replay"));
        capture.IdempotencyKey.Should().Be(requestId.ToString("D"));
        capture.Request.Should().Be(new ReplayDeadLetterRequest(reason));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, (int)OperatorReplayResult.EventNotFound)]
    [InlineData(HttpStatusCode.Conflict, (int)OperatorReplayResult.Conflict)]
    [InlineData(HttpStatusCode.BadRequest, (int)OperatorReplayResult.Rejected)]
    public async Task ReplayDeadLetterAsync_maps_expected_rejections(
        HttpStatusCode statusCode,
        int expected)
    {
        var capture = new ReplayCaptureHandler(statusCode);
        using var httpClient = CreateHttpClient(capture);
        var client = new OperatorApiClient(httpClient);

        var result = await client.ReplayDeadLetterAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Receiver recovered");

        result.Should().Be((OperatorReplayResult)expected);
    }

    [Fact]
    public async Task ReplayDeadLetterAsync_rejects_an_unexpected_status()
    {
        var capture = new ReplayCaptureHandler(HttpStatusCode.OK);
        using var httpClient = CreateHttpClient(capture);
        var client = new OperatorApiClient(httpClient);

        var action = () => client.ReplayDeadLetterAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Receiver recovered");

        var exception = await action.Should().ThrowAsync<HttpRequestException>();
        exception.Which.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler handler) => new(handler)
    {
        BaseAddress = new Uri("https://api.example.test/")
    };

    private static OperatorServerUpdateDraft CreateUpdateDraft() => new(
        Guid.NewGuid(),
        ExpectedRevision: 1,
        "Server",
        "game.example.test",
        QueryPort: 27015,
        RconPort: null,
        PollIntervalSeconds: 60,
        Notes: null);

    private static OperatorRconCredentialDraft CreateCredentialDraft() => new(
        Guid.NewGuid(),
        ExpectedServerRevision: 1,
        ExpectedCredentialRevision: 0,
        SecretAlias: "primary_server");

    private sealed class CaptureHandler(HttpStatusCode responseStatusCode) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }

        public Uri? RequestUri { get; private set; }

        public SayCommandRequest? Request { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            Request = await request.Content!.ReadFromJsonAsync<SayCommandRequest>(cancellationToken);
            return new HttpResponseMessage(responseStatusCode);
        }
    }

    private sealed class MapChangeCaptureHandler(HttpStatusCode responseStatusCode) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }

        public Uri? RequestUri { get; private set; }

        public ChangeMapCommandRequest? Request { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            Request = await request.Content!.ReadFromJsonAsync<ChangeMapCommandRequest>(
                cancellationToken);
            return new HttpResponseMessage(responseStatusCode);
        }
    }

    private sealed class ReplayCaptureHandler(HttpStatusCode responseStatusCode) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }

        public Uri? RequestUri { get; private set; }

        public string? IdempotencyKey { get; private set; }

        public ReplayDeadLetterRequest? Request { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            IdempotencyKey = request.Headers.GetValues("Idempotency-Key").Single();
            Request = await request.Content!.ReadFromJsonAsync<ReplayDeadLetterRequest>(cancellationToken);
            return new HttpResponseMessage(responseStatusCode);
        }
    }

    private sealed class LifecycleCaptureHandler(HttpStatusCode responseStatusCode) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }

        public Uri? RequestUri { get; private set; }

        public bool HasContent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            HasContent = request.Content is not null;
            return Task.FromResult(new HttpResponseMessage(responseStatusCode));
        }
    }

    private sealed class RegistrationCaptureHandler(
        HttpStatusCode responseStatusCode,
        ServerResponse? responseServer) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }

        public Uri? RequestUri { get; private set; }

        public string? IdempotencyKey { get; private set; }

        public RegisterServerRequest? Request { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            IdempotencyKey = request.Headers.GetValues("Idempotency-Key").Single();
            Request = await request.Content!.ReadFromJsonAsync<RegisterServerRequest>(cancellationToken);
            return new HttpResponseMessage(responseStatusCode)
            {
                Content = responseServer is null
                    ? null
                    : JsonContent.Create(responseServer)
            };
        }
    }

    private sealed class UpdateCaptureHandler(
        HttpStatusCode responseStatusCode,
        ServerResponse? responseServer) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }

        public Uri? RequestUri { get; private set; }

        public UpdateServerRequest? Request { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            Request = await request.Content!.ReadFromJsonAsync<UpdateServerRequest>(cancellationToken);
            return new HttpResponseMessage(responseStatusCode)
            {
                Content = responseServer is null
                    ? null
                    : JsonContent.Create(responseServer)
            };
        }
    }

    private sealed class CredentialCaptureHandler(
        HttpStatusCode responseStatusCode,
        ServerCredentialResponse? credential,
        string? problemCode) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }

        public Uri? RequestUri { get; private set; }

        public SetRconCredentialRequest? Request { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            Request = await request.Content!.ReadFromJsonAsync<SetRconCredentialRequest>(
                cancellationToken);
            return new HttpResponseMessage(responseStatusCode)
            {
                Content = credential is not null
                    ? JsonContent.Create(credential)
                    : problemCode is not null
                        ? JsonContent.Create(new { code = problemCode })
                        : new StringContent("{}")
            };
        }
    }
}
