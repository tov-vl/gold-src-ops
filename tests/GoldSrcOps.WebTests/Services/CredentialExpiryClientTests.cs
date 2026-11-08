using System.Net;
using System.Text;
using AwesomeAssertions;
using GoldSrcOps.Web.Services;

namespace GoldSrcOps.WebTests.Services;

public sealed class CredentialExpiryClientTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("{broken")]
    public async Task Null_and_malformed_responses_do_not_become_valid_empty_lists(string body)
    {
        using var handler = new FixtureHandler(body);
        using var http = new HttpClient(handler);
        http.BaseAddress = new Uri("https://api.example.test/");
        var client = new CredentialExpiryClient(http);
        var action = () => client.ReadAsync();
        await action.Should().ThrowAsync<Exception>();
        handler.Reads.Should().Be(1);
    }

    private sealed class FixtureHandler(string body) : HttpMessageHandler
    {
        public int Reads { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Reads++;
            request.RequestUri!.AbsolutePath.Should().Be("/api/operator/credential-expiry");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
