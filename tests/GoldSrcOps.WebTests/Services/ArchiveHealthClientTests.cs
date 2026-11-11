using System.Net;
using System.Text;
using AwesomeAssertions;
using GoldSrcOps.Web.Services;

namespace GoldSrcOps.WebTests.Services;

public sealed class ArchiveHealthClientTests
{
    [Theory]
    [InlineData("oversized", typeof(HttpRequestException))]
    [InlineData("missing-api", typeof(HttpRequestException))]
    [InlineData("malformed", typeof(System.Text.Json.JsonException))]
    [InlineData("null", typeof(InvalidDataException))]
    [InlineData("content-type", typeof(InvalidDataException))]
    [InlineData("charset", typeof(InvalidDataException))]
    public async Task Unusable_or_old_API_response_is_not_retried(string scenario, Type exceptionType)
    {
        using var handler = new FixtureHandler(scenario);
        using var client = new HttpClient(handler);
        client.BaseAddress = new Uri("https://api.test/");
        var source = new ArchiveHealthClient(client);
        var error = await source.Awaiting(static value => value.ReadAsync()).Should().ThrowAsync<Exception>();
        error.Which.Should().BeOfType(exceptionType);
        handler.Reads.Should().Be(1);
    }

    private sealed class FixtureHandler(string scenario) : HttpMessageHandler
    {
        public int Reads { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Reads++;
            request.RequestUri!.PathAndQuery.Should().Be("/api/operator/archive-health");
            request.Method.Should().Be(HttpMethod.Get);
            var response = new HttpResponseMessage(scenario is "missing-api" ? HttpStatusCode.NotFound : HttpStatusCode.OK)
            {
                Content = new StringContent(scenario switch { "oversized" => new string('x', 16385), "null" => "null", _ => "not json" }, Encoding.UTF8, "application/json")
            };
            switch (scenario)
            {
                case "content-type":
                    response.Content.Headers.ContentType!.MediaType = "text/html";
                    break;
                case "charset":
                    response.Content.Headers.ContentType!.CharSet = "private-sentinel";
                    break;
            }

            return Task.FromResult(response);
        }
    }
}
