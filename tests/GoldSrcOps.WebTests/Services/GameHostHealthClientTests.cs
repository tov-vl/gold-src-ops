using System.Net;
using System.Text;
using AwesomeAssertions;
using GoldSrcOps.Web.Services;

namespace GoldSrcOps.WebTests.Services;

public sealed class GameHostHealthClientTests
{
    [Fact]
    public async Task Older_api_without_warnings_does_not_become_an_empty_success()
    {
        using var handler = new LegacyHandler();
        using var client = new HttpClient(handler);
        client.BaseAddress = new Uri("https://api.example.test/");
        var result = await new GameHostHealthClient(client).ReadAsync();
        result.Warnings.Should().BeNull();
    }

    private sealed class LegacyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"evaluatedAtUtc":"2026-11-07T12:00:00Z","components":[],"values":[]}""", Encoding.UTF8, "application/json")
            });
    }
}
