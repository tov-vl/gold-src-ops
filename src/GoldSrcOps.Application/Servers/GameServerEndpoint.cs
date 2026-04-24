namespace GoldSrcOps.Application.Servers;

public sealed record GameServerEndpoint(string Host, int QueryPort, TimeSpan Timeout);
