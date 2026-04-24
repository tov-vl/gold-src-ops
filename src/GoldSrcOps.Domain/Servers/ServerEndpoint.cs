namespace GoldSrcOps.Domain.Servers;

public sealed class ServerEndpoint
{
    private ServerEndpoint()
    {
        Host = string.Empty;
    }

    public ServerEndpoint(string host, int queryPort, int? rconPort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queryPort);

        Host = host.Trim();
        QueryPort = queryPort;
        RconPort = rconPort;
    }

    public string Host { get; private set; }

    public int QueryPort { get; private set; }

    public int? RconPort { get; private set; }
}
