using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using GoldSrcOps.Contracts.Alerts;
using GoldSrcOps.Contracts.Commands;
using GoldSrcOps.Contracts.Credentials;
using GoldSrcOps.Contracts.Incidents;
using GoldSrcOps.Contracts.Monitoring;
using GoldSrcOps.Web.Services;
using Microsoft.AspNetCore.WebUtilities;

namespace GoldSrcOps.WebTests.Services;

public sealed class ReaderApiClientTests
{
    [Fact]
    public async Task GetFleetOverviewAsync_maps_triage_projection()
    {
        var observedAtUtc = new DateTimeOffset(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);
        var response = new FleetOverviewResponse(
            new DashboardOverviewResponse(1, 1, 0, 1, 0, 0, 0, observedAtUtc),
            [
                new FleetServerSummaryResponse(
                    Guid.Parse("6254f78a-5b16-41cf-aa9c-1167de84551a"),
                    "Dust2 Public",
                    "GoldSrc",
                    "game.example.test",
                    27015,
                    true,
                    30,
                    "Online",
                    observedAtUtc,
                    18,
                    "de_dust2",
                    4,
                    20,
                    0,
                    0,
                    0,
                    false,
                    false)
            ]);
        var capture = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(response)
        });
        using var httpClient = CreateHttpClient(capture);
        var client = new ReaderApiClient(httpClient);

        var result = await client.GetFleetOverviewAsync();

        result.Should().BeEquivalentTo(response);
        capture.RequestUri.Should().Be(new Uri("https://api.example.test/api/dashboard/fleet"));
    }

    [Fact]
    public async Task GetIncidentAsync_maps_incident_detail()
    {
        var incidentId = Guid.Parse("9307a87e-61cf-4901-8026-b301908431d6");
        var incident = new AvailabilityIncidentResponse(
            incidentId,
            Guid.Parse("f130f68c-cb3d-4e18-9dfe-7faf62ce8e3f"),
            "Unreachable",
            new DateTimeOffset(2026, 9, 4, 11, 45, 0, TimeSpan.Zero),
            null,
            "A2S query timed out",
            null,
            4);
        var capture = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(incident)
        });
        using var httpClient = CreateHttpClient(capture);
        var client = new ReaderApiClient(httpClient);

        var result = await client.GetIncidentAsync(incidentId);

        result.Should().Be(incident);
        capture.RequestUri.Should().Be(
            new Uri($"https://api.example.test/api/incidents/{incidentId:D}"));
    }

    [Fact]
    public async Task GetServerSnapshotsAsync_encodes_incident_context_window()
    {
        var serverId = Guid.Parse("f130f68c-cb3d-4e18-9dfe-7faf62ce8e3f");
        var fromUtc = new DateTimeOffset(2026, 9, 4, 11, 30, 0, TimeSpan.Zero);
        var toUtc = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
        var response = new SnapshotHistoryResponse(serverId, fromUtc, toUtc, 50, []);
        var capture = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(response)
        });
        using var httpClient = CreateHttpClient(capture);
        var client = new ReaderApiClient(httpClient);

        var result = await client.GetServerSnapshotsAsync(serverId, fromUtc, toUtc, 50);

        result.Should().BeEquivalentTo(response);
        capture.RequestUri.Should().NotBeNull();
        capture.RequestUri!.AbsolutePath.Should().Be($"/api/servers/{serverId:D}/snapshots");
        var query = QueryHelpers.ParseQuery(capture.RequestUri.Query);
        query["from"].Should().ContainSingle().Which.Should().Be(
            fromUtc.ToString("O", CultureInfo.InvariantCulture));
        query["to"].Should().ContainSingle().Which.Should().Be(
            toUtc.ToString("O", CultureInfo.InvariantCulture));
        query["limit"].Should().ContainSingle().Which.Should().Be("50");
    }

    [Fact]
    public async Task GetServerTrendAsync_sends_the_selected_bounded_window()
    {
        var serverId = Guid.Parse("f130f68c-cb3d-4e18-9dfe-7faf62ce8e3f");
        var fromUtc = new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
        var toUtc = fromUtc.AddDays(7);
        var response = new ServerTrendResponse(
            serverId,
            "7d",
            fromUtc,
            toUtc,
            BucketMinutes: 360,
            ObservedBuckets: 0,
            TotalBuckets: 28,
            SampleCount: 0,
            ReachableSampleCount: 0,
            ObservedReachabilityPercent: null,
            AverageLatencyMs: null,
            PeakPlayers: null,
            PeakBots: null,
            Buckets: []);
        var capture = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(response)
        });
        using var httpClient = CreateHttpClient(capture);
        var client = new ReaderApiClient(httpClient);

        var result = await client.GetServerTrendAsync(serverId, ServerTrendWindows.Last7Days);

        result.Should().BeEquivalentTo(response);
        capture.RequestUri.Should().NotBeNull();
        capture.RequestUri!.AbsolutePath.Should().Be($"/api/servers/{serverId:D}/trends");
        var query = QueryHelpers.ParseQuery(capture.RequestUri.Query);
        query["window"].Should().ContainSingle().Which.Should().Be("7d");
    }

    [Fact]
    public async Task GetServerTrendAsync_rejects_an_unsupported_window_before_sending()
    {
        var capture = new CaptureHandler(_ => throw new InvalidOperationException("A request was sent."));
        using var httpClient = CreateHttpClient(capture);
        var client = new ReaderApiClient(httpClient);

        var action = () => client.GetServerTrendAsync(Guid.NewGuid(), "30d");

        await action.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("window");
        capture.RequestUri.Should().BeNull();
    }

    [Fact]
    public async Task GetServerCredentialsAsync_maps_only_sanitized_metadata()
    {
        var serverId = Guid.Parse("6254f78a-5b16-41cf-aa9c-1167de84551a");
        var credential = new ServerCredentialResponse(
            Guid.Parse("df72cfde-7384-47f6-8ee9-9579404814e4"),
            serverId,
            3,
            "RconPassword",
            true,
            new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero));
        var capture = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new[] { credential })
        });
        using var httpClient = CreateHttpClient(capture);
        var client = new ReaderApiClient(httpClient);

        var result = await client.GetServerCredentialsAsync(serverId);

        result.Should().ContainSingle().Which.Should().Be(credential);
        capture.RequestUri.Should().Be(
            new Uri($"https://api.example.test/api/servers/{serverId:D}/credentials"));
    }

    [Fact]
    public async Task GetServerCommandsAsync_sends_limit_and_maps_response()
    {
        var serverId = Guid.Parse("6db654a7-0cb8-429f-b315-6c31d87a52e7");
        var command = new CommandExecutionResponse(
            Guid.Parse("9718efc0-f8de-4c20-ad02-f8652bd1523d"),
            serverId,
            "Say",
            "Succeeded",
            null,
            "operator",
            new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero),
            null,
            null,
            "Accepted",
            null);
        var capture = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new[] { command })
        });
        using var httpClient = CreateHttpClient(capture);
        var client = new ReaderApiClient(httpClient);

        var result = await client.GetServerCommandsAsync(serverId, 37);

        result.Should().ContainSingle().Which.Should().Be(command);
        capture.RequestUri.Should().Be(
            new Uri($"https://api.example.test/api/servers/{serverId:D}/commands?limit=37"));
    }

    [Fact]
    public async Task GetDeadLettersAsync_encodes_cursor_and_limit()
    {
        const string cursor = "2026-09-04T12:00:00Z|event/id?batch=1";
        var response = new DeadLetterListResponse(25, null, []);
        var capture = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(response)
        });
        using var httpClient = CreateHttpClient(capture);
        var client = new ReaderApiClient(httpClient);

        var result = await client.GetDeadLettersAsync(cursor, 25);

        result.Should().BeEquivalentTo(response);
        capture.RequestUri.Should().NotBeNull();
        capture.RequestUri!.AbsolutePath.Should().Be("/api/alert-delivery/dead-letters");
        var query = QueryHelpers.ParseQuery(capture.RequestUri.Query);
        query["limit"].Should().ContainSingle().Which.Should().Be("25");
        query["cursor"].Should().ContainSingle().Which.Should().Be(cursor);
    }

    [Fact]
    public async Task GetDeadLetterReplayAsync_maps_the_durable_receipt()
    {
        var requestId = Guid.Parse("85453d78-0e88-4b25-b376-c8991be0cbd5");
        var response = new DeadLetterReplayResponse(
            requestId,
            Guid.Parse("5793519a-789e-4183-9a3b-2d0ec0f89763"),
            "operator",
            new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero),
            "Receiver recovered",
            2,
            5,
            new DateTimeOffset(2026, 9, 8, 11, 0, 0, TimeSpan.Zero),
            "Pending",
            new DateTimeOffset(2026, 9, 8, 12, 1, 0, TimeSpan.Zero));
        var capture = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(response)
        });
        using var httpClient = CreateHttpClient(capture);
        var client = new ReaderApiClient(httpClient);

        var result = await client.GetDeadLetterReplayAsync(requestId);

        result.Should().Be(response);
        capture.RequestUri.Should().Be(
            new Uri($"https://api.example.test/api/alert-delivery/replays/{requestId:D}"));
    }

    [Theory]
    [InlineData("commands")]
    [InlineData("dead-letter")]
    [InlineData("replay")]
    [InlineData("credentials")]
    [InlineData("incident")]
    public async Task Optional_reader_resource_returns_null_for_not_found(string resource)
    {
        var capture = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var httpClient = CreateHttpClient(capture);
        var client = new ReaderApiClient(httpClient);

        object? result = resource switch
        {
            "commands" => await client.GetServerCommandsAsync(Guid.NewGuid(), 10),
            "dead-letter" => await client.GetDeadLetterAsync(Guid.NewGuid()),
            "replay" => await client.GetDeadLetterReplayAsync(Guid.NewGuid()),
            "incident" => await client.GetIncidentAsync(Guid.NewGuid()),
            _ => await client.GetServerCredentialsAsync(Guid.NewGuid())
        };

        result.Should().BeNull();
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler handler) => new(handler)
    {
        BaseAddress = new Uri("https://api.example.test/")
    };

    private sealed class CaptureHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(responseFactory(request));
        }
    }
}
