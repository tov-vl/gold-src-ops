using System.Diagnostics.Metrics;
using GoldSrcOps.Application.Common;
using GoldSrcOps.Application.Telemetry;
using Microsoft.Extensions.Hosting;

namespace GoldSrcOps.Infrastructure.Monitoring;

internal sealed class GoldSrcPollingMetrics : IHostedService, IDisposable
{
    private readonly GoldSrcPollingOptions _options;
    private readonly IClock _clock;
    private readonly Meter _meter;
    private readonly Lock _gate = new();
    private readonly double _started;
    private double _lastStarted;
    private double _lastFinished;
    private double _lastSuccessful;
    private double _lastFailure;
    private double _duration;
    private bool _failed;
    private bool _running;

    public GoldSrcPollingMetrics(GoldSrcPollingOptions options, IClock clock)
    {
        _options = options;
        _clock = clock;
        _started = Seconds(clock.UtcNow);
        // An observation budget, not a new timeout or a database-duration bound.
        CycleBudgetSeconds = Math.Max(300, 60 + 2 * (options.LoopDelay.TotalSeconds + options.BatchSize * options.QueryTimeout.TotalSeconds));
        _meter = new Meter(new MeterOptions(GoldSrcOpsMetrics.MeterName) { Scope = options });
        Gauge("enabled", () => options.Enabled ? 1 : 0);
        Gauge("started_timestamp", () => _started, "s");
        Gauge("observed_timestamp", () => Seconds(clock.UtcNow), "s");
        Gauge("cycle_budget", () => CycleBudgetSeconds, "s");
        Gauge("last_started_timestamp", () => Read().Started, "s");
        Gauge("last_finished_timestamp", () => Read().Finished, "s");
        Gauge("last_successful_cycle_timestamp", () => Read().Successful, "s");
        Gauge("last_failure_timestamp", () => Read().Failure, "s");
        Gauge("last_cycle_duration", () => Read().Duration, "s");
        Gauge("state", () => Read().State);
    }

    public double CycleBudgetSeconds { get; }

    // The always-registered hosted service initializes disabled-replica metrics too.
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void BeginCycle()
    {
        lock (_gate)
        {
            _running = true;
            _lastStarted = Seconds(_clock.UtcNow);
        }
    }

    public void CompleteCycle(bool failed, TimeSpan elapsed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);
        lock (_gate)
        {
            _lastFinished = Seconds(_clock.UtcNow);
            _duration = elapsed.TotalSeconds;
            _failed = failed;
            if (failed)
            {
                _lastFailure = _lastFinished;
            }
            else
            {
                _lastSuccessful = _lastFinished;
            }
        }
    }

    public void WorkerStopped()
    {
        lock (_gate)
        {
            _running = false;
        }
    }

    public void Dispose() => _meter.Dispose();

    private void Gauge(string name, Func<double> observe, string? unit = null) =>
        _meter.CreateObservableGauge("goldsrcops.polling." + name, observe, unit);

    private Observation Read()
    {
        lock (_gate)
        {
            var now = Seconds(_clock.UtcNow);
            var progress = _lastFinished > 0 ? _lastFinished : _started;
            var state = !_options.Enabled ? 3 : now < progress || now < _started || now < _lastStarted || now < _lastFailure || now < _lastSuccessful ? 0 :
                now - progress > CycleBudgetSeconds ? 4 : !_running || _lastFinished == 0 ? 0 : _failed ? 2 : 1;
            return new Observation(_lastStarted, _lastFinished, _lastSuccessful, _lastFailure, _duration, state);
        }
    }

    private static double Seconds(DateTimeOffset value) => (value - DateTimeOffset.UnixEpoch).TotalSeconds;

    private sealed record Observation(double Started, double Finished, double Successful, double Failure, double Duration, int State);
}
