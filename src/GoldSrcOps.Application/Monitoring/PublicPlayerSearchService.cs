namespace GoldSrcOps.Application.Monitoring;

// Singleton admission/cache; the caller supplies its scoped transport for this request only.
public sealed class PublicPlayerSearchService(PublicPlayerSearchSettings settings, TimeProvider timeProvider)
{
    private readonly Lock _sync = new();
    private readonly Dictionary<string, CachedResult> _cache = new(StringComparer.Ordinal);
    private bool _inFlight;
    private long? _lastAttempt;
    private TimeSpan _interval = TimeSpan.FromSeconds(10);

    public async Task<PublicPlayerSearchResult> SearchAsync(
        string? query, IPublicPlayerSearchSource source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!settings.Enabled)
        {
            return new(PublicPlayerSearchState.Disabled, null, false, []);
        }

        if (!PublicPlayerName.TryNormalize(query, out var name))
        {
            return new(PublicPlayerSearchState.Invalid, null, false, []);
        }

        lock (_sync)
        {
            var now = timeProvider.GetTimestamp();
            foreach (var key in _cache.Where(pair => timeProvider.GetElapsedTime(pair.Value.StoredAt, now) >= pair.Value.Lifetime)
                         .Select(static pair => pair.Key).ToArray())
            {
                _cache.Remove(key);
            }

            if (_cache.TryGetValue(name, out var cached))
            {
                return cached.Result;
            }

            var remaining = _lastAttempt is { } last ? _interval - timeProvider.GetElapsedTime(last, now) : TimeSpan.Zero;
            if (_inFlight || remaining > TimeSpan.Zero)
            {
                return PublicPlayerSearchResult.Busy((int)Math.Ceiling(Math.Max(1, remaining.TotalSeconds)));
            }

            _inFlight = true;
            _lastAttempt = now;
            _interval = TimeSpan.FromSeconds(10);
        }

        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3), timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            var result = await source.LookupAsync(name, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (result.State == PublicPlayerSearchState.Busy)
                {
                    // Respect addon-wide cooldown even when another API process consumed its slot.
                    var now = timeProvider.GetTimestamp();
                    var remaining = _interval - timeProvider.GetElapsedTime(_lastAttempt!.Value, now);
                    _lastAttempt = now;
                    _interval = TimeSpan.FromSeconds(Math.Max(remaining.TotalSeconds, Math.Clamp(result.RetryAfterSeconds ?? 10, 1, 10)));
                }
                else if (result.State == PublicPlayerSearchState.Fresh && result.CapturedAtUtc is { } captured)
                {
                    var age = timeProvider.GetUtcNow() - captured;
                    var lifetime = TimeSpan.FromSeconds(60) - (age > TimeSpan.Zero ? age : TimeSpan.Zero);
                    if (lifetime <= TimeSpan.Zero)
                    {
                        return PublicPlayerSearchResult.Unavailable;
                    }

                    if (_cache.Count == 128)
                    {
                        var oldest = _cache.MaxBy(pair => timeProvider.GetElapsedTime(pair.Value.StoredAt)).Key;
                        _cache.Remove(oldest);
                    }

                    result = result with { Entries = Array.AsReadOnly(result.Entries.ToArray()) };
                    _cache.Add(name, new(result, timeProvider.GetTimestamp(), lifetime));
                }
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Transport exceptions can contain credentials or private topology. Do not propagate them.
            return PublicPlayerSearchResult.Unavailable;
        }
        finally
        {
            lock (_sync)
            {
                _inFlight = false;
            }
        }
    }

    private sealed record CachedResult(PublicPlayerSearchResult Result, long StoredAt, TimeSpan Lifetime);
}
