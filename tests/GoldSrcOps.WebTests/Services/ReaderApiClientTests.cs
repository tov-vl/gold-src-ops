using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using GoldSrcOps.Contracts.Alerts;
using GoldSrcOps.Contracts.Commands;
using GoldSrcOps.Web.Services;
using Microsoft.AspNetCore.WebUtilities;

namespace GoldSrcOps.WebTests.Services;

public sealed class ReaderApiClientTests
{
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

    [Theory]
    [InlineData("commands")]
    [InlineData("dead-letter")]
    public async Task Optional_reader_resource_returns_null_for_not_found(string resource)
    {
        var capture = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var httpClient = CreateHttpClient(capture);
        var client = new ReaderApiClient(httpClient);

        object? result = string.Equals(resource, "commands", StringComparison.Ordinal)
            ? await client.GetServerCommandsAsync(Guid.NewGuid(), 10)
            : await client.GetDeadLetterAsync(Guid.NewGuid());

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
