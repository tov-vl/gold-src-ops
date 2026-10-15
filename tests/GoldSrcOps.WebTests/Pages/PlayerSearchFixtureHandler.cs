using System.Net;
using System.Net.Http.Json;
using GoldSrcOps.Contracts.Monitoring;

namespace GoldSrcOps.WebTests.Pages;

internal sealed class PlayerSearchFixtureHandler : HttpMessageHandler
{
    public int RequestCount { get; private set; }
    public string? LastName { get; private set; }
    public string? LastMatch { get; private set; }
    public Uri? LastUri { get; private set; }
    public HttpMethod? LastMethod { get; private set; }
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    public PublicPlayerSearchResponse? Result { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        LastUri = request.RequestUri;
        LastMethod = request.Method;
        var query = (await request.Content!.ReadFromJsonAsync<PublicPlayerSearchRequest>(cancellationToken))!;
        LastName = query.Name;
        LastMatch = query.Match;
        return new HttpResponseMessage(Status)
        {
            Content = JsonContent.Create(Result ?? new PublicPlayerSearchResponse("fresh", DateTimeOffset.UtcNow, false,
                [new(1, LastName, 1, 25, 8), new(2, LastName, 1, 25, 8)], null))
        };
    }
}
