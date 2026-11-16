using GoldSrcOps.Contracts.GameEvents;

namespace GoldSrcOps.TelemetryAcceptance;

internal sealed record DeliveryObservation(string PayloadSha256, int Status, GameEventIngestResponse? Receipt,
    GameEventIngestRequest? Request = null);
