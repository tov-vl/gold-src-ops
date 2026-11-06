using System.Globalization;
using System.Text.Json;
using GoldSrcOps.Application.Monitoring;

namespace GoldSrcOps.Infrastructure.Monitoring;

public sealed class GameHostHealthSource(HttpClient client) : IGameHostHealthSource
{
    private static readonly string Query = BuildQuery();

    public async Task<IReadOnlyList<GameHostHealthSample>?> ReadAsync(DateTimeOffset atUtc, CancellationToken cancellationToken)
    {
        if (client.BaseAddress is null)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/query")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["query"] = Query,
                    ["time"] = atUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                    ["timeout"] = "3s"
                })
            };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await response.Content.LoadIntoBufferAsync(65536, timeout.Token);
            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(timeout.Token));
            var root = document.RootElement;
            if (!string.Equals(root.GetProperty("status").GetString(), "success", StringComparison.Ordinal) || root.TryGetProperty("warnings", out _) ||
                !string.Equals(root.GetProperty("data").GetProperty("resultType").GetString(), "vector", StringComparison.Ordinal))
            {
                return null;
            }

            var result = new List<GameHostHealthSample>();
            foreach (var item in root.GetProperty("data").GetProperty("result").EnumerateArray())
            {
                if (result.Count >= 64)
                {
                    return null;
                }

                var labels = item.GetProperty("metric");
                var panel = int.Parse(labels.GetProperty("gso_panel").GetString()!, CultureInfo.InvariantCulture);
                var role = labels.TryGetProperty("role", out var roleLabel) ? roleLabel.GetString()! : string.Empty;
                var value = item.GetProperty("value");
                if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 2)
                {
                    return null;
                }

                var seconds = value[0].GetDouble();
                if (!double.IsFinite(seconds) || seconds is < 0 or > 253402300799)
                {
                    return null;
                }

                result.Add(new GameHostHealthSample(panel, role,
                    double.Parse(value[1].GetString()!, CultureInfo.InvariantCulture),
                    DateTimeOffset.UnixEpoch.AddSeconds(seconds)));
            }

            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException or
            KeyNotFoundException or FormatException or OverflowException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string BuildQuery()
    {
        using var stream = typeof(GameHostHealthSource).Assembly.GetManifestResourceStream("GameHostHealthDashboard.json")!;
        using var dashboard = JsonDocument.Parse(stream);
        var expressions = dashboard.RootElement.GetProperty("panels").EnumerateArray()
            .Where(static panel => panel.GetProperty("id").GetInt32() is >= 1 and <= 21)
            .Select(static panel => Label(panel.GetProperty("targets")[0].GetProperty("expr").GetString()!,
                panel.GetProperty("id").GetInt32())).ToList();
        string[] metrics = ["game_event_agent", "player_backup", "game_host_storage", "game_host_resource", "game_host_resource"];
        string[] services = ["game-event-agent", "player-backup", "game-host-storage", "game-host-resource", "game-host-resource"];
        for (var index = 0; index < metrics.Length; index++)
        {
            expressions.Add(Label($"goldsrcops_{metrics[index]}_observed_timestamp_seconds{{job=\"goldsrcops\",service_name=\"goldsrcops-{services[index]}-monitor\"}}", index + 24));
        }

        return string.Join(" or ", expressions);
    }

    private static string Label(string expression, int panel) =>
        $"label_replace(({expression}), \"gso_panel\", \"{panel.ToString(CultureInfo.InvariantCulture)}\", \"\", \"\")";
}
