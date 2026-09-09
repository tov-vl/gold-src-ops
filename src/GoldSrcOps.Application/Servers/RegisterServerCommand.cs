using GoldSrcOps.Domain.Servers;

namespace GoldSrcOps.Application.Servers;

public sealed record RegisterServerCommand(
    string Name,
    GameServerKind Game,
    string Host,
    int QueryPort,
    int? RconPort,
    int PollIntervalSeconds,
    string? Notes,
    bool IsEnabled = true,
    Guid? RegistrationRequestId = null);
