namespace GoldSrcOps.Infrastructure.Commands;

internal interface IGoldSrcRconClient
{
    Task<string> ExecuteAsync(GoldSrcRconRequest request, CancellationToken cancellationToken);
}
