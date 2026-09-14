namespace GoldSrcOps.Application.GameEvents;

public enum GameEventIngestResultKind
{
    Accepted = 1,
    Idempotent = 2,
    ServerNotFound = 3,
    EventIdConflict = 4,
    SourceSequenceConflict = 5,
    OccurredAtInFuture = 6
}

public sealed record GameEventIngestResult(
    GameEventIngestResultKind Kind,
    GameEventIngestDto? Event)
{
    public static GameEventIngestResult Accepted(GameEventIngestDto gameEvent) =>
        new(GameEventIngestResultKind.Accepted, gameEvent);

    public static GameEventIngestResult Idempotent(GameEventIngestDto gameEvent) =>
        new(GameEventIngestResultKind.Idempotent, gameEvent);

    public static GameEventIngestResult ServerNotFound() =>
        new(GameEventIngestResultKind.ServerNotFound, Event: null);

    public static GameEventIngestResult EventIdConflict() =>
        new(GameEventIngestResultKind.EventIdConflict, Event: null);

    public static GameEventIngestResult SourceSequenceConflict() =>
        new(GameEventIngestResultKind.SourceSequenceConflict, Event: null);

    public static GameEventIngestResult OccurredAtInFuture() =>
        new(GameEventIngestResultKind.OccurredAtInFuture, Event: null);
}
