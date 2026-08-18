using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace GoldSrcOps.UnitTests.Helpers;

internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<CapturedLogEntry> _entries = new();

    public IReadOnlyCollection<CapturedLogEntry> Entries => _entries.ToArray();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull =>
        NoopScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var properties = state is IEnumerable<KeyValuePair<string, object?>> values
            ? values.ToDictionary(
                static value => value.Key,
                static value => value.Value,
                StringComparer.Ordinal)
            : new Dictionary<string, object?>(StringComparer.Ordinal);

        _entries.Enqueue(new CapturedLogEntry(
            logLevel,
            eventId,
            formatter(state, exception),
            properties));
    }

    private sealed class NoopScope : IDisposable
    {
        public static NoopScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    internal sealed record CapturedLogEntry(
        LogLevel Level,
        EventId EventId,
        string Message,
        IReadOnlyDictionary<string, object?> Properties);
}
