namespace GoldSrcOps.TelemetryAcceptance;

internal sealed record QueueIdentity(string Source, long NextSequence, string EventId, long Sequence, string PayloadSha256);
