using System.Net.Http.Json;
using GoldSrcOps.Contracts.Monitoring;

namespace GoldSrcOps.Web.Services;

public sealed class PublicStatusClient(HttpClient httpClient, PublicApiMetrics metrics)
{
    public const string Last24HoursWindow = "24h";
    public const string Last7DaysWindow = "7d";

    public async Task<PublicStatusResponse> GetStatusAsync(CancellationToken cancellationToken = default) =>
        await GetResponseAsync<PublicStatusResponse>(
            "status", "api/public/status", "The public status response was empty.", false, cancellationToken)
        ?? throw new InvalidDataException("The public status response was empty.");

    public async Task<PublicA2sHistoryResponse> GetA2sHistoryAsync(
        string window,
        CancellationToken cancellationToken = default)
    {
        if (window is not Last24HoursWindow and not Last7DaysWindow)
        {
            throw new ArgumentException("Window must be either '24h' or '7d'.", nameof(window));
        }

        return await GetResponseAsync<PublicA2sHistoryResponse>(
            "history", $"api/public/a2s-history?window={window}",
            "The public A2S history response was empty.", false, cancellationToken)
            ?? throw new InvalidDataException("The public A2S history response was empty.");
    }

    public Task<PublicLeaderboardResponse?> GetLeaderboardAsync(CancellationToken cancellationToken = default) =>
        GetResponseAsync<PublicLeaderboardResponse>(
            "leaderboard", "api/public/leaderboard", "The public leaderboard response was empty.", true, cancellationToken);

    public Task<PublicServerJoinResponse?> GetServerJoinAsync(CancellationToken cancellationToken = default) =>
        GetResponseAsync<PublicServerJoinResponse>(
            "server", "api/public/server", "The public server join response was empty.", true, cancellationToken);

    private async Task<T?> GetResponseAsync<T>(
        string operation,
        string path,
        string emptyResponseMessage,
        bool allowNotFound,
        CancellationToken cancellationToken) where T : class
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var outcome = "error";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // ResponseHeadersRead ends HttpClient's timeout at the headers, so keep
        // the same configured budget alive through deserialization.
        deadline.CancelAfter(httpClient.Timeout);
        try
        {
            using var response = await httpClient.GetAsync(
                path, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (allowNotFound && response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                outcome = "not_found";
                return null;
            }

            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<T>(deadline.Token)
                ?? throw new InvalidDataException(emptyResponseMessage);
            deadline.Token.ThrowIfCancellationRequested();
            outcome = "success";
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = "canceled";
            throw;
        }
        catch (OperationCanceledException exception) when (
            deadline.IsCancellationRequested || exception.InnerException is TimeoutException)
        {
            outcome = "timeout";
            // Existing public-page fallbacks handle TaskCanceledException.
            throw new TaskCanceledException(
                "The public API response exceeded the configured timeout.",
                new TimeoutException("The complete public API response timed out.", exception),
                exception.CancellationToken);
        }
        finally
        {
            metrics.Record(operation, outcome, System.Diagnostics.Stopwatch.GetElapsedTime(started));
        }
    }
}
