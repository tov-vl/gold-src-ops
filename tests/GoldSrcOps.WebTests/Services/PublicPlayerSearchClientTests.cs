using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using GoldSrcOps.Contracts.Monitoring;
using GoldSrcOps.Web.Services;

namespace GoldSrcOps.WebTests.Services;

public sealed class PublicPlayerSearchClientTests
{
    [Theory]
    [InlineData(" ")]
    [InlineData("STEAM_0:1:12")]
    [InlineData("a\t")]
    [InlineData("a\u0085")]
    [InlineData("ИгрокИгрокИгрокИгрок")]
    public async Task Invalid_inputs_do_not_start_transport_or_record_query_metrics(string name)
    {
        using var metrics = new PublicApiMetrics();
        using var capture = new PublicApiMeasurementCapture(metrics);
        using var handler = new FixtureHandler(() => throw new InvalidOperationException("Transport must not run."));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test/") };
        var result = await new PublicPlayerSearchClient(http, metrics).SearchAsync(name);
        result.State.Should().Be("invalid");
        handler.Calls.Should().Be(0);
        capture.Values.Should().BeEmpty();
    }

    [Theory]
    [InlineData("wrong-name", "exact")]
    [InlineData("wrong-name", "contains")]
    [InlineData("rank-mismatch", "exact")]
    [InlineData("rank-mismatch", "contains")]
    [InlineData("too-many", "exact")]
    [InlineData("too-many", "contains")]
    [InlineData("truncated-short", "exact")]
    [InlineData("truncated-short", "contains")]
    [InlineData("stale", "exact")]
    [InlineData("stale", "contains")]
    [InlineData("future", "exact")]
    [InlineData("future", "contains")]
    [InlineData("zero-score", "exact")]
    [InlineData("zero-score", "contains")]
    [InlineData("wrong-order", "exact")]
    [InlineData("wrong-order", "contains")]
    [InlineData("null-rows", "exact")]
    [InlineData("null-rows", "contains")]
    [InlineData("wrong-status", "exact")]
    [InlineData("wrong-status", "contains")]
    [InlineData("busy-with-rows", "exact")]
    [InlineData("busy-with-rows", "contains")]
    [InlineData("unbounded-retry", "exact")]
    [InlineData("unbounded-retry", "contains")]
    public async Task Malformed_or_cross_query_response_never_renders_rows(string scenario, string match)
    {
        PublicPlayerSearchResponse valid = new("fresh", DateTimeOffset.UtcNow, false, [new(1, "Example", 1, 25, 1)], null);
        var result = scenario switch
        {
            "wrong-name" => valid with { Entries = [new(1, "Other", 1, 25, 1)] },
            "rank-mismatch" => valid with { Entries = [new(1, "Example", 4, 25, 1)] },
            "too-many" => valid with { Entries = Enumerable.Range(1, 11).Select(i => new PublicPlayerSearchEntryResponse(i, "Example", 1, 25, 1)).ToArray() },
            "truncated-short" => valid with { HasMore = true },
            "stale" => valid with { CapturedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-61) },
            "future" => valid with { CapturedAtUtc = DateTimeOffset.UtcNow.AddSeconds(40) },
            "zero-score" => valid with { Entries = [new(1, "Example", 0, 0, 0)] },
            "wrong-order" => valid with { Entries = [new(1, "Example", 1, 25, 1), new(2, "Example", 1, 26, 1)] },
            "null-rows" => valid with { Entries = null! },
            "busy-with-rows" => valid with { State = "busy", RetryAfterSeconds = 1 },
            "unbounded-retry" => new("busy", null, false, [], 10000),
            _ => valid
        };
        var status = scenario is "busy-with-rows" or "unbounded-retry" ? HttpStatusCode.TooManyRequests
            : scenario is "wrong-status" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK;
        using var metrics = new PublicApiMetrics();
        using var capture = new PublicApiMeasurementCapture(metrics);
        using var handler = new FixtureHandler(() => new(status) { Content = JsonContent.Create(result) });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test/") };
        var actual = await new PublicPlayerSearchClient(http, metrics).SearchAsync(string.Equals(match, "exact", StringComparison.Ordinal) ? "Example" : "amp", match);
        actual.State.Should().Be("unavailable");
        actual.Entries.Should().BeEmpty();
        handler.Calls.Should().Be(1);
        capture.Values.Should().ContainSingle().Which.Tags.Should().BeEquivalentTo(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["operation"] = "player_search",
            ["outcome"] = "error"
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Response_body_is_bounded_even_without_content_length(bool knownLength)
    {
        using var body = new CountingStream(new byte[16384]);
        using var handler = new FixtureHandler(() =>
        {
            var content = new StreamContent(body);
            content.Headers.ContentType = new("application/json");
            if (knownLength)
                content.Headers.ContentLength = 16384;
            return new(HttpStatusCode.OK) { Content = content };
        });
        using var metrics = new PublicApiMetrics();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test/") };
        (await new PublicPlayerSearchClient(http, metrics).SearchAsync("Example")).State.Should().Be("unavailable");
        body.BytesRead.Should().Be(knownLength ? 0 : 4097);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Body_timeout_or_caller_cancellation_disposes_stream_without_retry(bool cancelCaller)
    {
        using var body = new StalledStream();
        using var handler = new FixtureHandler(() =>
        {
            var content = new StreamContent(body);
            content.Headers.ContentType = new("application/json");
            return new(HttpStatusCode.OK) { Content = content };
        });
        using var metrics = new PublicApiMetrics();
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.example.test/"),
            Timeout = cancelCaller ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(200)
        };
        using var cancellation = new CancellationTokenSource();
        var pending = new PublicPlayerSearchClient(http, metrics).SearchAsync("Example", cancellation.Token);
        await body.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (cancelCaller)
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        else
        {
            (await pending.WaitAsync(TimeSpan.FromSeconds(3))).State.Should().Be("unavailable");
        }
        body.Disposed.Should().BeTrue();
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Exact_search_preserves_legacy_request_body_without_mode()
    {
        using var metrics = new PublicApiMetrics();
        using var handler = new FixtureHandler(() => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new PublicPlayerSearchResponse("fresh", DateTimeOffset.UtcNow, false, [], null))
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test/") };
        (await new PublicPlayerSearchClient(http, metrics).SearchAsync(" Abc ")).State.Should().Be("fresh");
        using var body = JsonDocument.Parse(handler.Body!);
        body.RootElement.EnumerateObject().Select(x => x.Name).Should().Equal("name");
        body.RootElement.GetProperty("name").GetString().Should().Be("Abc");
        handler.Calls.Should().Be(1);
    }

    [Theory]
    [InlineData("abc", null)]
    [InlineData("abc", "")]
    [InlineData("abc", "CONTAINS")]
    [InlineData("abc", "fuzzy")]
    [InlineData("ab", "contains")]
    [InlineData("😀😀", "contains")]
    [InlineData("invalid-unicode", "contains")]
    [InlineData("STEAM_abc", "contains")]
    public async Task Invalid_mode_or_partial_query_never_starts_transport(string query, string? match)
    {
        if (string.Equals(query, "invalid-unicode", StringComparison.Ordinal)) query = "abc" + (char)0xD800;
        using var metrics = new PublicApiMetrics();
        using var capture = new PublicApiMeasurementCapture(metrics);
        using var handler = new FixtureHandler(() => throw new InvalidOperationException("Transport must not run."));
        using var http = new HttpClient(handler);
        (await new PublicPlayerSearchClient(http, metrics).SearchAsync(query, match)).State.Should().Be("invalid");
        handler.Calls.Should().Be(0);
        capture.Values.Should().BeEmpty();
    }

    [Theory]
    [InlineData("abc", "[TAG]AbC", true)]
    [InlineData("рил", "КИРИЛЛ", true)]
    [InlineData("\u0451жи", "\u0401ЖИК", true)]
    [InlineData("ежи", "\u0401ЖИК", false)]
    [InlineData("café", "CAFÉ", false)]
    [InlineData("afe\u0301", "café", false)]
    [InlineData("STRASSE", "Straße", false)]
    [InlineData("АВС", "ABC", false)]
    [InlineData("Igr", "Игрок", false)]
    [InlineData("a*b", "[TAG]A*B", true)]
    [InlineData("a*c", "ABC", false)]
    [InlineData("😀😀😀", "😀😀😀Tail", true)]
    [InlineData("abc", "STEAM_abc", false)]
    [InlineData("abc", " abc ", false)]
    [InlineData("abc", "abc\n", false)]
    [InlineData("abc", "abc12345678901234567890123456789012", false)]
    public async Task Contains_validates_actual_names_with_fixed_case_map_and_preserves_duplicates(string query, string name, bool valid)
    {
        using var metrics = new PublicApiMetrics();
        using var handler = new FixtureHandler(() => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new PublicPlayerSearchResponse("fresh", DateTimeOffset.UtcNow, false,
                [new(1, name, 0, 1, 1), new(2, name, 0, 1, 1)], null))
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test/") };
        var result = await new PublicPlayerSearchClient(http, metrics).SearchAsync(" " + query + " ", "contains");
        result.State.Should().Be(valid ? "fresh" : "unavailable");
        if (valid) result.Entries.Select(x => x.Name).Should().Equal(name, name);
        else result.Entries.Should().BeEmpty();
        using var body = JsonDocument.Parse(handler.Body!);
        body.RootElement.GetProperty("name").GetString().Should().Be(query);
        body.RootElement.GetProperty("match").GetString().Should().Be("contains");
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Older_API_rejecting_contains_is_unavailable_without_retry_or_fallback()
    {
        using var metrics = new PublicApiMetrics();
        using var handler = new FixtureHandler(() => new(HttpStatusCode.BadRequest)
        {
            Content = JsonContent.Create(new PublicPlayerSearchResponse("invalid", null, false, [], null))
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test/") };
        (await new PublicPlayerSearchClient(http, metrics).SearchAsync("abc", "contains")).State.Should().Be("unavailable");
        handler.Calls.Should().Be(1);
    }

    private sealed class FixtureHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return response();
        }
    }

    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public int BytesRead { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await base.ReadAsync(buffer, cancellationToken);
            BytesRead += count;
            return count;
        }
    }

    private sealed class StalledStream : MemoryStream
    {
        public override bool CanSeek => false;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
