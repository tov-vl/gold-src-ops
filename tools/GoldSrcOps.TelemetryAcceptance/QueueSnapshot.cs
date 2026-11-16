namespace GoldSrcOps.TelemetryAcceptance;

internal sealed record QueueSnapshot(string Source, long NextSequence, int SpoolReceipts, IReadOnlyList<QueueEntry> Entries);
