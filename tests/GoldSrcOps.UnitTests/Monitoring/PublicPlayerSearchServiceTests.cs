using AwesomeAssertions;
using GoldSrcOps.Application.Monitoring;

namespace GoldSrcOps.UnitTests.Monitoring;

public sealed class PublicPlayerSearchServiceTests
{
    [Fact]
    public async Task Cache_is_exact_case_sensitive_expires_and_caches_empty_results()
    {
        var time = new SearchTime();
        var source = new SearchSource((_, _) => Task.FromResult(Fresh(time)));
        var service = new PublicPlayerSearchService(new(true, Guid.NewGuid()), time);
        (await service.SearchAsync(" A ", source, default)).State.Should().Be(PublicPlayerSearchState.Fresh);
        (await service.SearchAsync("A", source, default)).Entries.Should().BeEmpty();
        (await service.SearchAsync("a", source, default)).State.Should().Be(PublicPlayerSearchState.Busy);
        source.Calls.Should().Be(1);
        time.Advance(10);
        await service.SearchAsync("a", source, default);
        source.Calls.Should().Be(2);
        time.Advance(50);
        source.Action = (_, _) => Task.FromResult(PublicPlayerSearchResult.Unavailable);
        (await service.SearchAsync("A", source, default)).State.Should().Be(PublicPlayerSearchState.Unavailable);
        (await service.SearchAsync("a", source, default)).State.Should().Be(PublicPlayerSearchState.Fresh);
        source.Calls.Should().Be(3);
    }

    [Fact]
    public async Task Concurrent_misses_never_queue_or_duplicate_and_cached_hits_remain_available()
    {
        var time = new SearchTime();
        var source = new SearchSource((_, _) => Task.FromResult(Fresh(time)));
        var service = new PublicPlayerSearchService(new(true, Guid.NewGuid()), time);
        await service.SearchAsync("cached", source, default);
        time.Advance(10);
        var pending = new TaskCompletionSource<PublicPlayerSearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Action = (_, _) => pending.Task;
        var first = service.SearchAsync("A", source, default);
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => service.SearchAsync(i % 2 == 0 ? "A" : "B", source, default)));
        results.Should().OnlyContain(result => result.State == PublicPlayerSearchState.Busy);
        (await service.SearchAsync("cached", source, default)).State.Should().Be(PublicPlayerSearchState.Fresh);
        source.Calls.Should().Be(2);
        pending.SetResult(Fresh(time));
        await first;
    }

    [Fact]
    public async Task Errors_cancellation_and_timeout_release_inflight_without_resetting_cooldown()
    {
        var time = new SearchTime();
        var source = new SearchSource((_, _) => throw new InvalidOperationException("private transport detail"));
        var service = new PublicPlayerSearchService(new(true, Guid.NewGuid()), time);
        (await service.SearchAsync("A", source, default)).Should().Be(PublicPlayerSearchResult.Unavailable);
        (await service.SearchAsync("B", source, default)).State.Should().Be(PublicPlayerSearchState.Busy);
        source.Calls.Should().Be(1);
        time.Advance(10);
        using var cancellation = new CancellationTokenSource();
        source.Action = async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Fresh(time); };
        var pending = service.SearchAsync("B", source, cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        (await service.SearchAsync("C", source, default)).State.Should().Be(PublicPlayerSearchState.Busy);
        time.Advance(10);
        // Real timer is intentional: the source must observe the linked three-second deadline.
        (await service.SearchAsync("C", source, default)).State.Should().Be(PublicPlayerSearchState.Unavailable);
        source.Calls.Should().Be(3);
        time.Advance(10);
        source.Action = (_, _) => Task.FromResult(Fresh(time));
        (await service.SearchAsync("C", source, default)).State.Should().Be(PublicPlayerSearchState.Fresh);
    }

    [Fact]
    public async Task Addon_busy_extends_admission_and_failures_do_not_publish_cross_query_data()
    {
        var time = new SearchTime();
        var source = new SearchSource((_, _) => { time.Advance(2); return Task.FromResult(PublicPlayerSearchResult.Busy(10)); });
        var service = new PublicPlayerSearchService(new(true, Guid.NewGuid()), time);
        await service.SearchAsync("A", source, default);
        time.Advance(8);
        (await service.SearchAsync("B", source, default)).RetryAfterSeconds.Should().Be(2);
        source.Calls.Should().Be(1);
        time.Advance(2);
        source.Action = (_, _) => Task.FromResult(Fresh(time));
        (await service.SearchAsync("B", source, default)).State.Should().Be(PublicPlayerSearchState.Fresh);
        source.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Cache_lifetime_starts_at_capture_and_published_rows_cannot_be_mutated_by_source()
    {
        var time = new SearchTime();
        var rows = new List<PublicPlayerSearchEntry> { new(1, "A", 0, 1, 0) };
        var source = new SearchSource((_, _) => Task.FromResult(new PublicPlayerSearchResult(PublicPlayerSearchState.Fresh, time.GetUtcNow().AddSeconds(-59), false, rows)));
        var service = new PublicPlayerSearchService(new(true, Guid.NewGuid()), time);
        await service.SearchAsync("A", source, default);
        rows.Clear();
        (await service.SearchAsync("A", source, default)).Entries.Should().HaveCount(1);
        time.Advance(1);
        (await service.SearchAsync("A", source, default)).State.Should().Be(PublicPlayerSearchState.Busy);
        time.Advance(9);
        source.Action = (_, _) => Task.FromResult(Fresh(time) with { CapturedAtUtc = time.GetUtcNow().AddSeconds(-60) });
        (await service.SearchAsync("A", source, default)).State.Should().Be(PublicPlayerSearchState.Unavailable);
    }

    [Fact]
    public async Task Disabled_invalid_and_pre_cancelled_requests_never_reach_source()
    {
        var time = new SearchTime();
        var source = new SearchSource((_, _) => throw new InvalidOperationException());
        var disabled = new PublicPlayerSearchService(new(false, Guid.Empty), time);
        (await disabled.SearchAsync("A", source, default)).State.Should().Be(PublicPlayerSearchState.Disabled);
        var service = new PublicPlayerSearchService(new(true, Guid.NewGuid()), time);
        (await service.SearchAsync("STEAM_1", source, default)).State.Should().Be(PublicPlayerSearchState.Invalid);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SearchAsync("A", source, new CancellationToken(true)));
        source.Calls.Should().Be(0);
    }

    private static PublicPlayerSearchResult Fresh(SearchTime time) => new(PublicPlayerSearchState.Fresh, time.GetUtcNow(), false, []);
}

internal sealed class SearchTime : TimeProvider
{
    private long _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _ticks;
    public override DateTimeOffset GetUtcNow() => PublicPlayerSearchProtocolTests.Now.AddTicks(_ticks);
    public void Advance(int seconds) => _ticks += TimeSpan.FromSeconds(seconds).Ticks;
}

internal sealed class SearchSource(Func<string, CancellationToken, Task<PublicPlayerSearchResult>> action) : IPublicPlayerSearchSource
{
    public int Calls { get; private set; }
    public Func<string, CancellationToken, Task<PublicPlayerSearchResult>> Action { get; set; } = action;
    public Task<PublicPlayerSearchResult> LookupAsync(string name, CancellationToken cancellationToken)
    {
        Calls++;
        return Action(name, cancellationToken);
    }
}
