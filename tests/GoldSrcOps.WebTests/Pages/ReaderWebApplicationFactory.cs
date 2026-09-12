using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using GoldSrcOps.Contracts.Alerts;
using GoldSrcOps.Contracts.Commands;
using GoldSrcOps.Contracts.Credentials;
using GoldSrcOps.Contracts.Incidents;
using GoldSrcOps.Contracts.Monitoring;
using GoldSrcOps.Contracts.Servers;
using GoldSrcOps.Web.Security;
using GoldSrcOps.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GoldSrcOps.WebTests.Pages;

internal sealed class DisabledAuthenticationWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["GoldSrcOpsApi:BaseUrl"] = "https://api.example.test/",
                    ["Authentication:Enabled"] = "false"
                });
        });
    }
}

internal sealed class ReaderWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string? role;
    private readonly string? subject;

    public static readonly Guid ServerId = Guid.Parse("f130f68c-cb3d-4e18-9dfe-7faf62ce8e3f");
    public static readonly Guid OfflineServerId = Guid.Parse("851718aa-1725-4b90-b5c3-61112952db35");
    public static readonly Guid StaleServerId = Guid.Parse("9204994a-66f6-42af-90ca-96fb6f5a671f");
    public static readonly Guid PausedServerId = Guid.Parse("4657e3aa-c7b0-4c8b-9a1f-fd9e37c88a7d");
    public static readonly Guid OpenIncidentId = Guid.Parse("9307a87e-61cf-4901-8026-b301908431d6");
    public static readonly Guid CommandId = Guid.Parse("17477e4e-97bb-4c50-a046-08fa5cd48dca");
    public static readonly Guid DeadLetterEventId = Guid.Parse("70d51faf-6029-4b1e-a922-b7a3ab8d1f84");
    public static readonly Guid ReplayRequestId = Guid.Parse("4fb7401c-802c-48b9-aa71-5e27619b0784");
    public const string ServerName = "Reader fixture server";
    public const string OfflineServerName = "Bravo outage server";
    public const string StaleServerName = "Charlie stale server";
    public const string PausedServerName = "Delta maintenance server";
    public const string OpenIncidentReason = "A2S query timed out";
    public const string CommandResultSummary = "Command accepted by the server";
    public const string CommandPayloadSentinel = "fixture-command-payload-must-not-render";
    public const string DeadLetterLastError = "Webhook endpoint returned a terminal response";
    public const string DeadLetterPayloadSentinel = "fixture-dead-letter-payload-must-not-render";
    public const string ReplayReason = "Receiver health was verified by the operator";
    public const string Subject = "reader-portal-fixture";

    public FixtureReaderApiClient ReaderApiClient { get; }

    public FixtureOperatorApiClient OperatorApiClient { get; } = new();

    public ReaderWebApplicationFactory(
        string? role = WebSecurity.ReaderRole,
        string? subject = Subject,
        bool serverEnabled = true,
        bool commandInProgress = false,
        bool rconConfigured = true)
    {
        this.role = role;
        this.subject = subject;
        ReaderApiClient = new FixtureReaderApiClient(
            serverEnabled,
            commandInProgress,
            rconConfigured);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["GoldSrcOpsApi:BaseUrl"] = "https://api.example.test/",
                    ["Authentication:Enabled"] = "false"
                });
        });
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
                    options.DefaultForbidScheme = TestAuthenticationHandler.SchemeName;
                })
                .AddScheme<TestAuthenticationOptions, TestAuthenticationHandler>(
                    TestAuthenticationHandler.SchemeName,
                    options =>
                    {
                        options.Role = role;
                        options.Subject = subject;
                    });
            services.RemoveAll<WebAuthenticationState>();
            services.AddSingleton(new WebAuthenticationState(true));
            services.RemoveAll<IReaderApiClient>();
            services.AddSingleton<IReaderApiClient>(ReaderApiClient);
            services.RemoveAll<IOperatorApiClient>();
            services.AddSingleton<IOperatorApiClient>(OperatorApiClient);
        });
    }

    internal sealed class FixtureReaderApiClient(
        bool serverEnabled = true,
        bool commandInProgress = false,
        bool rconConfigured = true) : IReaderApiClient
    {
        private static readonly DateTimeOffset ObservedAtUtc =
            new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

        public bool CommandInProgress { get; set; } = commandInProgress;

        public Task<DashboardOverviewResponse> GetOverviewAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DashboardOverviewResponse(1, 1, 0, 1, 0, 0, 0, ObservedAtUtc));

        public Task<FleetOverviewResponse> GetFleetOverviewAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new FleetOverviewResponse(
                new DashboardOverviewResponse(4, 3, 1, 2, 1, 1, 2, ObservedAtUtc),
                [
                    new FleetServerSummaryResponse(
                        ServerId,
                        ServerName,
                        "GoldSrc",
                        "game.example.test",
                        27015,
                        true,
                        30,
                        "Online",
                        ObservedAtUtc,
                        18,
                        "de_dust2",
                        0,
                        20,
                        0,
                        0,
                        0,
                        false,
                        false),
                    new FleetServerSummaryResponse(
                        OfflineServerId,
                        OfflineServerName,
                        "GoldSrc",
                        "offline.example.test",
                        27016,
                        true,
                        30,
                        "Offline",
                        ObservedAtUtc.AddMinutes(-5),
                        null,
                        null,
                        null,
                        null,
                        null,
                        4,
                        1,
                        false,
                        true),
                    new FleetServerSummaryResponse(
                        StaleServerId,
                        StaleServerName,
                        "GoldSrc",
                        "stale.example.test",
                        27017,
                        true,
                        30,
                        "Online",
                        ObservedAtUtc.AddMinutes(-10),
                        24,
                        "de_inferno",
                        3,
                        20,
                        0,
                        0,
                        0,
                        true,
                        true),
                    new FleetServerSummaryResponse(
                        PausedServerId,
                        PausedServerName,
                        "GoldSrc",
                        "paused.example.test",
                        27018,
                        false,
                        60,
                        "Unknown",
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        0,
                        1,
                        false,
                        true)
                ]));

        public Task<IReadOnlyList<ServerResponse>> GetServersAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ServerResponse>>([CreateServer()]);

        public Task<ServerResponse?> GetServerAsync(
            Guid serverId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(serverId == ServerId ? CreateServer() : null);

        public Task<ServerStatusResponse?> GetServerStatusAsync(
            Guid serverId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ServerStatusResponse?>(serverId == ServerId
                ? new ServerStatusResponse(
                    ServerId,
                    "Online",
                    true,
                    ObservedAtUtc,
                    ObservedAtUtc,
                    18,
                    "de_dust2",
                    0,
                    20,
                    null,
                    0)
                : null);

        public Task<IReadOnlyList<AvailabilityIncidentResponse>> GetOpenIncidentsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AvailabilityIncidentResponse>>([CreateOpenIncident()]);

        public Task<IReadOnlyList<AvailabilityIncidentResponse>> GetServerIncidentsAsync(
            Guid serverId,
            int limit,
            CancellationToken cancellationToken = default)
        {
            if (serverId != ServerId)
            {
                return Task.FromResult<IReadOnlyList<AvailabilityIncidentResponse>>([]);
            }

            IReadOnlyList<AvailabilityIncidentResponse> incidents =
            [
                CreateOpenIncident(),
                new AvailabilityIncidentResponse(
                    Guid.Parse("5e3fd38c-a3c8-4a2d-a8a2-ac8fd7b9a788"),
                    ServerId,
                    "Unreachable",
                    ObservedAtUtc.AddHours(-3),
                    ObservedAtUtc.AddHours(-2),
                    "Connection refused",
                    "Probe recovered",
                    3)
            ];

            return Task.FromResult<IReadOnlyList<AvailabilityIncidentResponse>>(incidents.Take(limit).ToArray());
        }

        public Task<IReadOnlyList<CommandExecutionResponse>?> GetServerCommandsAsync(
            Guid serverId,
            int limit,
            CancellationToken cancellationToken = default)
        {
            if (serverId != ServerId)
            {
                return Task.FromResult<IReadOnlyList<CommandExecutionResponse>?>(null);
            }

            var commands = new List<CommandExecutionResponse>();
            if (CommandInProgress)
            {
                commands.Add(new CommandExecutionResponse(
                    Guid.Parse("d59e885e-090b-4404-8954-d5d623ba13c8"),
                    ServerId,
                    "Say",
                    "Running",
                    CommandPayloadSentinel,
                    "operator-fixture",
                    ObservedAtUtc.AddMinutes(-1),
                    ObservedAtUtc,
                    null,
                    null,
                    null));
            }

            commands.AddRange(
            [
                new(
                    CommandId,
                    ServerId,
                    "Say",
                    "Succeeded",
                    CommandPayloadSentinel,
                    "operator-fixture",
                    ObservedAtUtc.AddMinutes(-10),
                    ObservedAtUtc.AddMinutes(-9),
                    ObservedAtUtc.AddMinutes(-9),
                    CommandResultSummary,
                    null),
                new(
                    Guid.Parse("5f780c3f-30b0-4509-88db-73034fefbd2e"),
                    ServerId,
                    "Restart",
                    "Failed",
                    "restart",
                    "operator-fixture",
                    ObservedAtUtc.AddMinutes(-45),
                    ObservedAtUtc.AddMinutes(-44),
                    ObservedAtUtc.AddMinutes(-43),
                    null,
                    "RCON acknowledgement timed out")
            ]);

            return Task.FromResult<IReadOnlyList<CommandExecutionResponse>?>(commands.Take(limit).ToArray());
        }

        public Task<IReadOnlyList<ServerCredentialResponse>?> GetServerCredentialsAsync(
            Guid serverId,
            CancellationToken cancellationToken = default)
        {
            if (serverId != ServerId)
            {
                return Task.FromResult<IReadOnlyList<ServerCredentialResponse>?>(null);
            }

            IReadOnlyList<ServerCredentialResponse> credentials = rconConfigured
                ?
                [
                    new ServerCredentialResponse(
                        Guid.Parse("c6b3cf17-c64c-4eaa-85ca-bd0224b28722"),
                        ServerId,
                        3,
                        "RconPassword",
                        true,
                        ObservedAtUtc.AddDays(-1),
                        ObservedAtUtc.AddHours(-2))
                ]
                : [];

            return Task.FromResult<IReadOnlyList<ServerCredentialResponse>?>(credentials);
        }

        public Task<DeadLetterListResponse> GetDeadLettersAsync(
            string? cursor,
            int limit,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<DeadLetterListItemResponse> items =
            [
                new(
                    DeadLetterEventId,
                    "AvailabilityIncidentOpened",
                    1,
                    "Server",
                    ServerId,
                    ObservedAtUtc.AddMinutes(-20),
                    5,
                    1,
                    ObservedAtUtc.AddMinutes(-15),
                    DeadLetterLastError)
            ];

            return Task.FromResult(new DeadLetterListResponse(limit, "fixture-next-cursor", items));
        }

        public Task<DeadLetterDetailResponse?> GetDeadLetterAsync(
            Guid eventId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<DeadLetterDetailResponse?>(eventId == DeadLetterEventId
                ? new DeadLetterDetailResponse(
                    DeadLetterEventId,
                    "AvailabilityIncidentOpened",
                    1,
                    "Server",
                    ServerId,
                    ObservedAtUtc.AddMinutes(-20),
                    JsonSerializer.SerializeToElement(new { content = DeadLetterPayloadSentinel }),
                    5,
                    1,
                    ObservedAtUtc.AddMinutes(-15),
                    DeadLetterLastError,
                    true,
                    Guid.Parse("0b6749f3-2114-478a-b0bd-8a336f876175"),
                    "Delivered",
                    ObservedAtUtc.AddMinutes(-5))
                : null);

        public Task<DeadLetterReplayResponse?> GetDeadLetterReplayAsync(
            Guid requestId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<DeadLetterReplayResponse?>(requestId == ReplayRequestId
                ? new DeadLetterReplayResponse(
                    ReplayRequestId,
                    DeadLetterEventId,
                    "operator-fixture",
                    ObservedAtUtc.AddMinutes(-2),
                    ReplayReason,
                    2,
                    5,
                    ObservedAtUtc.AddMinutes(-15),
                    "Pending",
                    ObservedAtUtc.AddMinutes(-1))
                : null);

        public Task<SnapshotHistoryResponse?> GetServerSnapshotsAsync(
            Guid serverId,
            int limit,
            CancellationToken cancellationToken = default)
        {
            if (serverId != ServerId)
            {
                return Task.FromResult<SnapshotHistoryResponse?>(null);
            }

            PollSnapshotResponse[] snapshots =
            [
                new(
                    Guid.Parse("dbe590f4-cf68-48b5-b865-8c1951a526bf"),
                    ServerId,
                    ObservedAtUtc,
                    true,
                    18,
                    "de_dust2",
                    0,
                    20,
                    0,
                    "1.1.2.7/Stdio",
                    null),
                new(
                    Guid.Parse("73e87c47-9aef-4a95-b10c-47d8751d381e"),
                    ServerId,
                    ObservedAtUtc.AddMinutes(-30),
                    false,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    OpenIncidentReason)
            ];

            return Task.FromResult<SnapshotHistoryResponse?>(new SnapshotHistoryResponse(
                ServerId,
                null,
                null,
                limit,
                snapshots.Take(limit).ToArray()));
        }

        private ServerResponse CreateServer() => new(
            ServerId,
            7,
            ServerName,
            "cstrike",
            "game.example.test",
            27015,
            27015,
            serverEnabled,
            30,
            "Reader fixture note",
            ObservedAtUtc.AddDays(-1));

        private static AvailabilityIncidentResponse CreateOpenIncident() => new(
            OpenIncidentId,
            ServerId,
            "Unreachable",
            ObservedAtUtc.AddMinutes(-15),
            null,
            OpenIncidentReason,
            null,
            4);
    }

    internal sealed class FixtureOperatorApiClient : IOperatorApiClient
    {
        private int callCount;
        private int mapChangeCallCount;
        private int restartCallCount;
        private int monitoringCallCount;
        private int registrationCallCount;
        private int replayCallCount;
        private int updateCallCount;
        private int credentialUpdateCallCount;

        public int CallCount => Volatile.Read(ref callCount);

        public int MapChangeCallCount => Volatile.Read(ref mapChangeCallCount);

        public int RestartCallCount => Volatile.Read(ref restartCallCount);

        public int ReplayCallCount => Volatile.Read(ref replayCallCount);

        public int MonitoringCallCount => Volatile.Read(ref monitoringCallCount);

        public int RegistrationCallCount => Volatile.Read(ref registrationCallCount);

        public int UpdateCallCount => Volatile.Read(ref updateCallCount);

        public int CredentialUpdateCallCount => Volatile.Read(ref credentialUpdateCallCount);

        public Guid? LastServerId { get; private set; }

        public Guid? LastMapChangeServerId { get; private set; }

        public Guid? LastRestartServerId { get; private set; }

        public string? LastMessage { get; private set; }

        public string? LastMap { get; private set; }

        public Guid? LastMonitoringServerId { get; private set; }

        public bool? LastMonitoringEnabled { get; private set; }

        public OperatorServerRegistrationDraft? LastRegistrationDraft { get; private set; }

        public OperatorServerUpdateDraft? LastUpdateDraft { get; private set; }

        public OperatorRconCredentialDraft? LastCredentialUpdateDraft { get; private set; }

        public Guid? LastEventId { get; private set; }

        public Guid? LastRequestId { get; private set; }

        public string? LastReason { get; private set; }

        public OperatorCommandQueueResult Result { get; set; } = OperatorCommandQueueResult.Queued;

        public Exception? ExceptionToThrow { get; set; }

        public OperatorCommandQueueResult MapChangeResult { get; set; } =
            OperatorCommandQueueResult.Queued;

        public Exception? MapChangeExceptionToThrow { get; set; }

        public OperatorCommandQueueResult RestartResult { get; set; } =
            OperatorCommandQueueResult.Queued;

        public Exception? RestartExceptionToThrow { get; set; }

        public OperatorMonitoringUpdateResult MonitoringResult { get; set; } =
            OperatorMonitoringUpdateResult.Updated;

        public Exception? MonitoringExceptionToThrow { get; set; }

        public OperatorServerRegistrationResult RegistrationResult { get; set; } =
            new(
                OperatorServerRegistrationResultKind.Created,
                new ServerResponse(
                    ServerId,
                    1,
                    ServerName,
                    "GoldSrc",
                    "game.example.test",
                    27015,
                    null,
                    false,
                    60,
                    null,
                    new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero)));

        public Exception? RegistrationExceptionToThrow { get; set; }

        public OperatorServerUpdateResult UpdateResult { get; set; } =
            new(
                OperatorServerUpdateResultKind.Updated,
                new ServerResponse(
                    ServerId,
                    8,
                    ServerName,
                    "GoldSrc",
                    "game.example.test",
                    27015,
                    null,
                    false,
                    60,
                    null,
                    new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero)));

        public Exception? UpdateExceptionToThrow { get; set; }

        public OperatorRconCredentialUpdateResult CredentialUpdateResult { get; set; } =
            new(
                OperatorRconCredentialUpdateResultKind.Updated,
                new ServerCredentialResponse(
                    Guid.Parse("c6b3cf17-c64c-4eaa-85ca-bd0224b28722"),
                    ServerId,
                    4,
                    "RconPassword",
                    true,
                    new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 9, 10, 10, 0, 0, TimeSpan.Zero)));

        public Exception? CredentialUpdateExceptionToThrow { get; set; }

        public OperatorReplayResult ReplayResult { get; set; } = OperatorReplayResult.Accepted;

        public Exception? ReplayExceptionToThrow { get; set; }

        public Task<OperatorCommandQueueResult> QueueSayAsync(
            Guid serverId,
            string message,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref callCount);
            LastServerId = serverId;
            LastMessage = message;

            if (ExceptionToThrow is not null)
            {
                return Task.FromException<OperatorCommandQueueResult>(ExceptionToThrow);
            }

            return Task.FromResult(Result);
        }

        public Task<OperatorCommandQueueResult> QueueMapChangeAsync(
            Guid serverId,
            string map,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref mapChangeCallCount);
            LastMapChangeServerId = serverId;
            LastMap = map;

            if (MapChangeExceptionToThrow is not null)
            {
                return Task.FromException<OperatorCommandQueueResult>(
                    MapChangeExceptionToThrow);
            }

            return Task.FromResult(MapChangeResult);
        }

        public Task<OperatorCommandQueueResult> QueueRestartAsync(
            Guid serverId,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref restartCallCount);
            LastRestartServerId = serverId;

            if (RestartExceptionToThrow is not null)
            {
                return Task.FromException<OperatorCommandQueueResult>(RestartExceptionToThrow);
            }

            return Task.FromResult(RestartResult);
        }

        public Task<OperatorServerRegistrationResult> RegisterServerAsync(
            OperatorServerRegistrationDraft draft,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref registrationCallCount);
            LastRegistrationDraft = draft;

            if (RegistrationExceptionToThrow is not null)
            {
                return Task.FromException<OperatorServerRegistrationResult>(
                    RegistrationExceptionToThrow);
            }

            return Task.FromResult(RegistrationResult);
        }

        public Task<OperatorReplayResult> ReplayDeadLetterAsync(
            Guid eventId,
            Guid requestId,
            string reason,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref replayCallCount);
            LastEventId = eventId;
            LastRequestId = requestId;
            LastReason = reason;

            if (ReplayExceptionToThrow is not null)
            {
                return Task.FromException<OperatorReplayResult>(ReplayExceptionToThrow);
            }

            return Task.FromResult(ReplayResult);
        }

        public Task<OperatorServerUpdateResult> UpdateServerAsync(
            OperatorServerUpdateDraft draft,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref updateCallCount);
            LastUpdateDraft = draft;

            if (UpdateExceptionToThrow is not null)
            {
                return Task.FromException<OperatorServerUpdateResult>(UpdateExceptionToThrow);
            }

            return Task.FromResult(UpdateResult);
        }

        public Task<OperatorRconCredentialUpdateResult> SetRconCredentialAsync(
            OperatorRconCredentialDraft draft,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref credentialUpdateCallCount);
            LastCredentialUpdateDraft = draft;

            if (CredentialUpdateExceptionToThrow is not null)
            {
                return Task.FromException<OperatorRconCredentialUpdateResult>(
                    CredentialUpdateExceptionToThrow);
            }

            return Task.FromResult(CredentialUpdateResult);
        }

        public Task<OperatorMonitoringUpdateResult> SetMonitoringEnabledAsync(
            Guid serverId,
            bool enabled,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref monitoringCallCount);
            LastMonitoringServerId = serverId;
            LastMonitoringEnabled = enabled;

            if (MonitoringExceptionToThrow is not null)
            {
                return Task.FromException<OperatorMonitoringUpdateResult>(MonitoringExceptionToThrow);
            }

            return Task.FromResult(MonitoringResult);
        }
    }

    private sealed class TestAuthenticationOptions : AuthenticationSchemeOptions
    {
        public string? Role { get; set; }

        public string? Subject { get; set; }
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<TestAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<TestAuthenticationOptions>(options, logger, encoder)
    {
        public const string SchemeName = "ReaderTest";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
                CreateClaims(Options.Role, Options.Subject),
                SchemeName,
                ClaimTypes.Name,
                ClaimTypes.Role);
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }

        protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
        {
            Response.Redirect("/auth/forbidden");
            return Task.CompletedTask;
        }

        private static IEnumerable<Claim> CreateClaims(string? role, string? subject)
        {
            yield return new Claim(ClaimTypes.Name, "Portal user");
            if (!string.IsNullOrWhiteSpace(subject))
            {
                yield return new Claim(WebSecurity.SubjectClaim, subject);
            }

            if (!string.IsNullOrWhiteSpace(role))
            {
                yield return new Claim(ClaimTypes.Role, role);
            }
        }
    }
}
