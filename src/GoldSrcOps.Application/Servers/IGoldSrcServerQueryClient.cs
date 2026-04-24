namespace GoldSrcOps.Application.Servers;

public interface IGoldSrcServerQueryClient
{
    Task<GameServerInfo> QueryInfoAsync(GameServerEndpoint endpoint, CancellationToken cancellationToken);
}
