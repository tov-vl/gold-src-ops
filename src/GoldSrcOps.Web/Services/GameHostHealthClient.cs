using GoldSrcOps.Contracts.Monitoring;

namespace GoldSrcOps.Web.Services;

internal sealed class GameHostHealthClient(HttpClient client) : IGameHostHealthClient
{
    public async Task<GameHostHealthResponse> ReadAsync(CancellationToken cancellationToken = default) =>
        await client.GetFromJsonAsync<GameHostHealthResponse>("api/operator/game-host-health", cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidDataException("Game-host health response is unavailable.");
}
