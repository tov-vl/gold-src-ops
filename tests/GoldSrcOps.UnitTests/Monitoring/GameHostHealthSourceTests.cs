using System.Net;
using System.Text;
using AwesomeAssertions;
using GoldSrcOps.Infrastructure.Monitoring;

namespace GoldSrcOps.UnitTests.Monitoring;

public sealed class GameHostHealthSourceTests
{
    [Fact]
    public async Task Fixed_query_reuses_dashboard_and_drops_private_labels()
    {
        using var handler = new FixtureHandler("""{"status":"success","data":{"resultType":"vector","result":[{"metric":{"gso_panel":"1","instance":"private-host","role":""},"value":[1793966400,"1"]}]}}""");
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://prometheus.test/") };
        var source = new GameHostHealthSource(client);
        var result = await source.ReadAsync(DateTimeOffset.FromUnixTimeSeconds(1793966400), CancellationToken.None);
        result.Should().ContainSingle();
        result![0].Value.Should().Be(1);
        handler.Query.Should().Contain("gso_panel").And.Contain("goldsrcops_storage:observation_valid").And.Contain("GameHostMemoryLow");
        handler.Path.Should().Be("/api/v1/query");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("{\"status\":\"success\",\"data\":{\"resultType\":\"vector\",\"result\":[{\"metric\":{\"gso_panel\":\"1\"},\"value\":[]}]}}")]
    [InlineData("{\"status\":\"error\"}")]
    [InlineData("{\"status\":\"success\",\"warnings\":[\"partial\"]}")]
    [InlineData("{\"status\":\"success\",\"data\":{\"resultType\":\"matrix\"}}")]
    public async Task Invalid_or_partial_response_is_unavailable(string body)
    {
        using var handler = new FixtureHandler(body);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://prometheus.test/") };
        (await new GameHostHealthSource(client).ReadAsync(DateTimeOffset.UtcNow, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Disabled_source_performs_no_request()
    {
        using var handler = new FixtureHandler("{}");
        using var client = new HttpClient(handler);
        (await new GameHostHealthSource(client).ReadAsync(DateTimeOffset.UtcNow, CancellationToken.None)).Should().BeNull();
        handler.Path.Should().BeNull();
    }

    [Fact]
    public async Task Oversized_response_is_unavailable()
    {
        using var handler = new FixtureHandler(new string('x', 65537));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://prometheus.test/") };
        (await new GameHostHealthSource(client).ReadAsync(DateTimeOffset.UtcNow, CancellationToken.None)).Should().BeNull();
    }

    private sealed class FixtureHandler(string body) : HttpMessageHandler
    {
        public string? Query { get; private set; }
        public string? Path { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri!.AbsolutePath;
            request.Method.Should().Be(HttpMethod.Post);
            Query = Uri.UnescapeDataString(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
