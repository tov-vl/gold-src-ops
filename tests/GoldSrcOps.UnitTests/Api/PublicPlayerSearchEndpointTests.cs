using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using GoldSrcOps.Application.Monitoring;
using GoldSrcOps.Contracts.Monitoring;
using GoldSrcOps.UnitTests.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GoldSrcOps.UnitTests.Api;

public sealed class PublicPlayerSearchEndpointTests
{
    [Fact]
    public async Task Anonymous_post_exposes_only_safe_fields_preserves_duplicates_and_reuses_cache()
    {
        const string name = "Я <>&\"";
        var source = new SearchSource((query, _) =>
        {
            query.Should().Be(name);
            return Task.FromResult(new PublicPlayerSearchResult(PublicPlayerSearchState.Fresh, DateTimeOffset.UtcNow, false,
                [new(1, name, 0, 1, 1), new(2, name, 0, 1, 1)]));
        });
        await using var factory = CreateFactory(source);
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/public/players/search", new PublicPlayerSearchRequest(name));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var result = await response.Content.ReadFromJsonAsync<PublicPlayerSearchResponse>();
        result!.State.Should().Be("fresh");
        result.Entries.Should().HaveCount(2).And.OnlyContain(row => row.Name == name);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("state", "capturedAtUtc", "hasMore", "entries", "retryAfterSeconds");
        json.RootElement.GetProperty("entries")[0].EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("ordinal", "name", "rank", "kills", "deaths");
        using var cached = await client.PostAsJsonAsync("/api/public/players/search", new PublicPlayerSearchRequest(name));
        cached.StatusCode.Should().Be(HttpStatusCode.OK);
        source.Calls.Should().Be(1);
        using var get = await client.GetAsync("/api/public/players/search?name=A");
        get.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        source.Calls.Should().Be(1);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{bad}")]
    [InlineData("{\"name\":null}")]
    [InlineData("{\"name\":\"STEAM_1\"}")]
    [InlineData("{\"name\":\"A\\n\"}")]
    [InlineData("{\"name\":\"\\uD800\"}")]
    [InlineData("{\"name\":\"A\",\"host\":\"untrusted\"}")]
    public async Task Invalid_body_is_rejected_without_source_access_or_echo(string body)
    {
        var source = new SearchSource((_, _) => throw new InvalidOperationException());
        await using var factory = CreateFactory(source);
        using var client = factory.CreateClient();
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/api/public/players/search", content);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("STEAM_").And.NotContain("untrusted");
        source.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Oversized_and_chunked_bodies_are_bounded_and_other_media_types_rejected()
    {
        var source = new SearchSource((_, _) => throw new InvalidOperationException());
        await using var factory = CreateFactory(source);
        using var client = factory.CreateClient();
        using var large = new StringContent(new string('A', 257), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/api/public/players/search", large);
        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        using var chunked = new ChunkedContent();
        using var chunkedResponse = await client.PostAsync("/api/public/players/search", chunked);
        chunkedResponse.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        using var text = new StringContent("A");
        using var textResponse = await client.PostAsync("/api/public/players/search", text);
        textResponse.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        using var utf16 = new StringContent("{}", Encoding.Unicode, "application/json");
        using var encodingResponse = await client.PostAsync("/api/public/players/search", utf16);
        encodingResponse.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        source.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Malformed_utf8_and_content_type_fail_without_source_access()
    {
        var source = new SearchSource((_, _) => throw new InvalidOperationException());
        await using var factory = CreateFactory(source);
        using var client = factory.CreateClient();
        using var invalidText = new ByteArrayContent([.. Encoding.ASCII.GetBytes("{\"name\":\""), 0xFF, .. Encoding.ASCII.GetBytes("\"}")]);
        invalidText.Headers.ContentType = new("application/json");
        using var textResponse = await client.PostAsync("/api/public/players/search", invalidText);
        textResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var invalidType = new ByteArrayContent("{}"u8.ToArray());
        invalidType.Headers.TryAddWithoutValidation("Content-Type", "???");
        using var typeResponse = await client.PostAsync("/api/public/players/search", invalidType);
        typeResponse.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        source.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Busy_unavailable_and_disabled_have_explicit_status_without_private_errors()
    {
        var source = new SearchSource((_, _) => throw new InvalidOperationException("private-secret private-host raw-frame"));
        await using var factory = CreateFactory(source);
        using var client = factory.CreateClient();
        using var unavailable = await client.PostAsJsonAsync("/api/public/players/search", new PublicPlayerSearchRequest("A"));
        unavailable.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await unavailable.Content.ReadAsStringAsync()).Should().NotContain("private").And.NotContain("raw-frame");
        using var busy = await client.PostAsJsonAsync("/api/public/players/search", new PublicPlayerSearchRequest("B"));
        busy.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        busy.Headers.RetryAfter!.Delta.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(10));
        source.Calls.Should().Be(1);
        await using var disabled = new GoldSrcOpsApiFactory(principal: TestApiPrincipal.Anonymous);
        using var disabledClient = disabled.CreateClient();
        using var missing = await disabledClient.PostAsJsonAsync("/api/public/players/search", new PublicPlayerSearchRequest("A"));
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Admission_is_global_before_body_parsing_and_cannot_be_bypassed_by_forwarded_addresses()
    {
        var source = new SearchSource((_, _) => throw new InvalidOperationException());
        await using var factory = CreateFactory(source);
        using var client = factory.CreateClient();
        for (var index = 0; index < 30; index++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/public/players/search");
            request.Headers.Add("X-Forwarded-For", $"192.0.2.{index}");
            request.Content = new StringContent("{bad}", Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        using var rejected = await client.PostAsJsonAsync("/api/public/players/search", new PublicPlayerSearchRequest("A"));
        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        rejected.Headers.CacheControl!.NoStore.Should().BeTrue();
        rejected.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(10));
        source.Calls.Should().Be(0);
    }

    private static GoldSrcOpsApiFactory CreateFactory(IPublicPlayerSearchSource source) => new(
        configureTestServices: services =>
        {
            services.RemoveAll<IPublicPlayerSearchSource>();
            services.AddSingleton(source);
        },
        principal: TestApiPrincipal.Anonymous,
        configurationOverrides: new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PublicPlayerSearch:Enabled"] = "true",
            ["PublicServerJoin:ServerId"] = Guid.NewGuid().ToString(),
            ["PublicServerJoin:Name"] = "Fixture",
            ["PublicServerJoin:Host"] = "play.example.test",
            ["PublicServerJoin:Port"] = "27015"
        });

    private sealed class ChunkedContent : HttpContent
    {
        public ChunkedContent() => Headers.ContentType = new("application/json");
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(new byte[257], 0, 257);
    }
}
