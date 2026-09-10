using GoldSrcOps.Domain.Servers;

namespace GoldSrcOps.Application.Servers;

public enum RegisterServerResultKind
{
    Created,
    Idempotent,
    IdempotencyConflict
}

public sealed record RegisterServerResult(
    RegisterServerResultKind Kind,
    ServerDto? Server)
{
    public static RegisterServerResult Created(ServerDto server) =>
        new(RegisterServerResultKind.Created, server);

    public static RegisterServerResult Idempotent(ServerDto server) =>
        new(RegisterServerResultKind.Idempotent, server);

    public static RegisterServerResult IdempotencyConflict() =>
        new(RegisterServerResultKind.IdempotencyConflict, Server: null);
}

public sealed record ServerRegistrationPersistenceResult(
    bool WasCreated,
    Server Server);
