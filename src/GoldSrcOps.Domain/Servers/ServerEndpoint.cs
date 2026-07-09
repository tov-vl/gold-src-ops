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

        if (queryPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(queryPort), "Query port must be between 1 and 65535.");
        }

        if (rconPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(rconPort), "RCON port must be between 1 and 65535.");
        }

        Host = host.Trim();
        QueryPort = queryPort;
        RconPort = rconPort;
    }

    public string Host { get; private set; }

    public int QueryPort { get; private set; }

    public int? RconPort { get; private set; }
}
