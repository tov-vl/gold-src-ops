using System.Globalization;
using System.Text.Json;
using GoldSrcOps.Application.Monitoring;

namespace GoldSrcOps.Infrastructure.Monitoring;

public sealed class ArchiveHealthSource(HttpClient client) : IArchiveHealthSource
{
    private static readonly string Query = string.Join(" or ", new[] { "observation_success", "observed_timestamp_seconds", "has_success", "last_success_timestamp_seconds" }
        .Select(static name => $"label_replace(last_over_time(goldsrcops_archive_schedule_{name}{{job=\"goldsrcops\",service_name=\"goldsrcops-archive-schedule-monitor\"}}[30m]), \"gso_archive_field\", \"{name}\", \"\", \"\")"));

    public async Task<ArchiveHealthSnapshot> ReadAsync(DateTimeOffset atUtc, CancellationToken cancellationToken)
    {
        if (client.BaseAddress is null)
        {
            return new ArchiveHealthSnapshot(Metrics: null, Rules: null);
        }

        using var metrics = new HttpRequestMessage(HttpMethod.Post, "api/v1/query");
        metrics.Content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["query"] = Query,
            ["time"] = atUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            ["timeout"] = "3s"
        });
        using var rules = new HttpRequestMessage(HttpMethod.Get, "api/v1/rules?type=alert&exclude_alerts=true&rule_group%5B%5D=goldsrcops-archive-schedule");
        var metricsTask = ReadAsync(metrics, ParseMetrics, cancellationToken);
        var rulesTask = ReadAsync(rules, ParseRules, cancellationToken);
        await Task.WhenAll(metricsTask, rulesTask).ConfigureAwait(false);
        return new ArchiveHealthSnapshot(await metricsTask.ConfigureAwait(false), await rulesTask.ConfigureAwait(false));
    }

    private async Task<T?> ReadAsync<T>(HttpRequestMessage request, Func<JsonElement, T?> parse, CancellationToken cancellationToken) where T : class
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await response.Content.LoadIntoBufferAsync(32768, timeout.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false));
            var root = document.RootElement;
            return root.GetProperty("status").GetString() is "success" && !root.TryGetProperty("warnings", out _) && !root.TryGetProperty("infos", out _)
                ? parse(root.GetProperty("data")) : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or
            FormatException or OverflowException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static IReadOnlyList<ArchiveMetricSample>? ParseMetrics(JsonElement data)
    {
        if (data.GetProperty("resultType").GetString() is not "vector" || data.GetProperty("result").GetArrayLength() > 8)
        {
            return null;
        }

        var result = new List<ArchiveMetricSample>();
        foreach (var sample in data.GetProperty("result").EnumerateArray())
        {
            var value = sample.GetProperty("value");
            if (sample.TryGetProperty("histogram", out _) || value.GetArrayLength() != 2)
            {
                return null;
            }

            var seconds = value[0].GetDouble();
            if (!double.IsFinite(seconds) || seconds is < 0 or > 253402300799)
            {
                return null;
            }

            var number = value[1].GetString() is { Length: > 0 and <= 64 } text ? text : throw new JsonException("Invalid numeric observation.");
            result.Add(new ArchiveMetricSample(RequiredString(sample.GetProperty("metric"), "gso_archive_field"),
                double.Parse(number, CultureInfo.InvariantCulture), DateTimeOffset.UnixEpoch.AddSeconds(seconds)));
        }

        return result;
    }

    private static IReadOnlyList<ArchiveRuleSample>? ParseRules(JsonElement data)
    {
        var groups = data.GetProperty("groups");
        if (data.TryGetProperty("groupNextToken", out _) || groups.GetArrayLength() != 1)
        {
            return null;
        }

        var group = groups[0];
        var rules = group.GetProperty("rules");
        if (rules.GetArrayLength() != 4)
        {
            return null;
        }

        var result = new List<ArchiveRuleSample>(4);
        foreach (var rule in rules.EnumerateArray())
        {
            if (RequiredString(rule, "type") is not "alerting")
            {
                return null;
            }

            var labels = rule.GetProperty("labels");
            result.Add(new ArchiveRuleSample(RequiredString(rule, "name"), RequiredString(group, "name"),
                RequiredString(labels, "component"), RequiredString(labels, "severity"), RequiredString(rule, "state"),
                RequiredString(rule, "health"), rule.GetProperty("lastEvaluation").GetDateTimeOffset()));
        }

        return result;
    }

    private static string RequiredString(JsonElement item, string name) =>
        item.GetProperty(name).GetString() is { Length: > 0 and <= 128 } value ? value : throw new JsonException("Invalid fixed observation field.");
}
