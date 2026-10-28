namespace GoldSrcOps.GameEventAgent;

internal sealed partial class GameEventDeliveryWorker : BackgroundService
{
    private readonly IGameEventOutbox _outbox;
    private readonly GameEventDispatcher _dispatcher;
    private readonly GameEventDeliveryOptions _options;
    private readonly ILogger<GameEventDeliveryWorker> _logger;
    private readonly GameEventAgentHealth _health;

    public GameEventDeliveryWorker(
        IGameEventOutbox outbox,
        GameEventDispatcher dispatcher,
        GameEventDeliveryOptions options,
        ILogger<GameEventDeliveryWorker> logger,
        GameEventAgentHealth health)
    {
        _outbox = outbox;
        _dispatcher = dispatcher;
        _options = options;
        _logger = logger;
        _health = health;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _health.RecordStart(import: false);
            _outbox.Initialize();
            LogStarted(_logger, _options.DispatchInterval, _options.BatchSize,
                _options.MaximumAttempts, _options.MaximumEventAge);
            while (!stoppingToken.IsCancellationRequested)
            {
                _health.RecordStart(import: false);
                try
                {
                    var summary = await _dispatcher.DispatchAvailableAsync(stoppingToken).ConfigureAwait(false);
                    _health.RecordCompletion(import: false, summary.Retried > 0 ? "retrying"
                        : summary.DeadLettered > 0 ? "blocked" : summary.Processed == 0 ? "idle" : "progress");
                    if (summary.Processed > 0)
                    {
                        LogDispatchCompleted(_logger, summary.Acknowledged, summary.Retried, summary.DeadLettered);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _health.RecordCompletion(import: false, "failed");
                    LogDispatchFailed(_logger, exception);
                }

                try
                {
                    await Task.Delay(_options.DispatchInterval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            _health.RecordStop(import: false);
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Game-event delivery worker started with interval {DispatchInterval}, batch size {BatchSize}, maximum attempts {MaximumAttempts}, and maximum event age {MaximumEventAge}.")]
    private static partial void LogStarted(
        ILogger logger,
        TimeSpan dispatchInterval,
        int batchSize,
        int maximumAttempts,
        TimeSpan maximumEventAge);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "Game-event dispatch pass completed: {Acknowledged} acknowledged, {Retried} rescheduled, and {DeadLettered} dead-lettered.")]
    private static partial void LogDispatchCompleted(
        ILogger logger,
        int acknowledged,
        int retried,
        int deadLettered);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Error,
        Message = "Game-event dispatch pass failed; the active lease will permit a later retry.")]
    private static partial void LogDispatchFailed(ILogger logger, Exception exception);
}
