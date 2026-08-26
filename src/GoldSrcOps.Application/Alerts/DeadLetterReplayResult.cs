namespace GoldSrcOps.Application.Alerts;

public enum DeadLetterReplayResultKind
{
    Accepted = 1,
    Idempotent = 2,
    EventNotFound = 3,
    EventNotDeadLetter = 4,
    NewerEventProcessing = 5,
    IdempotencyConflict = 6,
    EventNotReplayable = 7
}

public sealed record DeadLetterReplayResult(
    DeadLetterReplayResultKind Kind,
    DeadLetterReplayRecordDto? Replay)
{
    public static DeadLetterReplayResult Accepted(DeadLetterReplayRecordDto replay) =>
        new(DeadLetterReplayResultKind.Accepted, replay);

    public static DeadLetterReplayResult Idempotent(DeadLetterReplayRecordDto replay) =>
        new(DeadLetterReplayResultKind.Idempotent, replay);

    public static DeadLetterReplayResult EventNotFound() =>
        new(DeadLetterReplayResultKind.EventNotFound, Replay: null);

    public static DeadLetterReplayResult EventNotDeadLetter() =>
        new(DeadLetterReplayResultKind.EventNotDeadLetter, Replay: null);

    public static DeadLetterReplayResult NewerEventProcessing() =>
        new(DeadLetterReplayResultKind.NewerEventProcessing, Replay: null);

    public static DeadLetterReplayResult IdempotencyConflict() =>
        new(DeadLetterReplayResultKind.IdempotencyConflict, Replay: null);

    public static DeadLetterReplayResult EventNotReplayable() =>
        new(DeadLetterReplayResultKind.EventNotReplayable, Replay: null);
}
