using System.Text.Json;
using GoldSrcOps.Application.Monitoring;

namespace GoldSrcOps.Infrastructure.Monitoring;

public sealed class GameHostWarningSource(HttpClient client) : IGameHostWarningSource
{
    private static readonly string[] Groups = ["goldsrcops-game-event-agent", "goldsrcops-player-backup", "goldsrcops-game-host-storage", "goldsrcops-game-host-resources"];
    private static readonly string Path = "api/v1/rules?type=alert&exclude_alerts=true&" +
        string.Join('&', Groups.Select(static group => "rule_group%5B%5D=" + group));

    public async Task<IReadOnlyList<GameHostWarningSample>?> ReadAsync(CancellationToken cancellationToken)
    {
        if (client.BaseAddress is null)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var response = await client.GetAsync(Path, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await response.Content.LoadIntoBufferAsync(65536, timeout.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false));
            var root = document.RootElement;
            if (!string.Equals(root.GetProperty("status").GetString(), "success", StringComparison.Ordinal) || root.TryGetProperty("warnings", out _))
            {
                return null;
            }

            var data = root.GetProperty("data");
            if (data.TryGetProperty("groupNextToken", out _))
            {
                return null;
            }

            var groups = data.GetProperty("groups");
            if (groups.GetArrayLength() != Groups.Length)
            {
                return null;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<GameHostWarningSample>(17);
            foreach (var group in groups.EnumerateArray())
            {
                var name = RequiredString(group, "name");
                if (!Groups.Contains(name, StringComparer.Ordinal) || !seen.Add(name))
                {
                    return null;
                }

                foreach (var rule in group.GetProperty("rules").EnumerateArray())
                {
                    if (result.Count >= 17 || !string.Equals(rule.GetProperty("type").GetString(), "alerting", StringComparison.Ordinal))
                    {
                        return null;
                    }

                    var labels = rule.GetProperty("labels");
                    result.Add(new GameHostWarningSample(name, RequiredString(rule, "name"),
                        RequiredString(labels, "component"), RequiredString(labels, "severity"),
                        RequiredString(rule, "state"), RequiredString(rule, "health"),
                        rule.GetProperty("lastEvaluation").GetDateTimeOffset()));
                }
            }

            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return null;
        }
    }

    private static string RequiredString(JsonElement value, string property) =>
        value.GetProperty(property).GetString() is { Length: > 0 } text ? text : throw new JsonException("Missing rule field.");
}
