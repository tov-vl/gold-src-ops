namespace GoldSrcOps.Application.Servers;

public enum UpdateServerResultKind
{
    Updated,
    NotFound,
    MonitoringEnabled,
    RevisionConflict
}

public sealed record UpdateServerResult(
    UpdateServerResultKind Kind,
    ServerDto? Server)
{
    public static UpdateServerResult Updated(ServerDto server) =>
        new(UpdateServerResultKind.Updated, server);

    public static UpdateServerResult NotFound() =>
        new(UpdateServerResultKind.NotFound, Server: null);

    public static UpdateServerResult MonitoringEnabled() =>
        new(UpdateServerResultKind.MonitoringEnabled, Server: null);

    public static UpdateServerResult RevisionConflict() =>
        new(UpdateServerResultKind.RevisionConflict, Server: null);
}

public enum SetServerEnabledResultKind
{
    Updated,
    NotFound,
    RevisionConflict
}

public sealed record SetServerEnabledResult(
    SetServerEnabledResultKind Kind,
    ServerDto? Server)
{
    public static SetServerEnabledResult Updated(ServerDto server) =>
        new(SetServerEnabledResultKind.Updated, server);

    public static SetServerEnabledResult NotFound() =>
        new(SetServerEnabledResultKind.NotFound, Server: null);

    public static SetServerEnabledResult RevisionConflict() =>
        new(SetServerEnabledResultKind.RevisionConflict, Server: null);
}
