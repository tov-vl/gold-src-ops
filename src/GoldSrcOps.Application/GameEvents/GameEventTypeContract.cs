using GoldSrcOps.Domain.GameEvents;

namespace GoldSrcOps.Application.GameEvents;

public static class GameEventTypeContract
{
    public static bool TryParse(string? value, out GameEventType eventType)
    {
        switch (value?.Trim())
        {
            case "server.started":
                eventType = GameEventType.ServerStarted;
                return true;
            case "server.stopped":
                eventType = GameEventType.ServerStopped;
                return true;
            case "map.started":
                eventType = GameEventType.MapStarted;
                return true;
            case "round.started":
                eventType = GameEventType.RoundStarted;
                return true;
            case "round.ended":
                eventType = GameEventType.RoundEnded;
                return true;
            default:
                eventType = default;
                return false;
        }
    }

    public static string ToWireValue(GameEventType eventType) => eventType switch
    {
        GameEventType.ServerStarted => "server.started",
        GameEventType.ServerStopped => "server.stopped",
        GameEventType.MapStarted => "map.started",
        GameEventType.RoundStarted => "round.started",
        GameEventType.RoundEnded => "round.ended",
        _ => throw new ArgumentOutOfRangeException(
            nameof(eventType),
            eventType,
            "Game event type is not supported.")
    };
}
