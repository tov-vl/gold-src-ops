using System.Globalization;
using System.Text.Json;
using GoldSrcOps.Application.Monitoring;

namespace GoldSrcOps.Infrastructure.Monitoring;

public sealed class CommandDispatchHealthSource(HttpClient client) : ICommandDispatchHealthSource
{
    private static readonly string Query = string.Join(" or ", new[] { "enabled", "started_timestamp_seconds", "observed_timestamp_seconds", "cycle_budget_seconds", "last_started_timestamp_seconds", "last_finished_timestamp_seconds", "state", "last_successful_cycle_timestamp_seconds", "last_failure_timestamp_seconds", "last_cycle_duration_seconds", "consecutive_failures", "in_progress" }
        .Select(static name => $"label_replace(goldsrcops_command_dispatch_worker_{name}{{job=\"goldsrcops\",service_name=\"GoldSrcOps\"}}, \"gso_command_dispatch_field\", \"{name}\", \"\", \"\")"));

    public async Task<CommandDispatchHealthSnapshot> ReadAsync(DateTimeOffset atUtc, CancellationToken cancellationToken)
    {
        if (client.BaseAddress is null)
        {
            return new CommandDispatchHealthSnapshot(Metrics: null, Rules: null);
        }

        using var metrics = new HttpRequestMessage(HttpMethod.Post, "api/v1/query");
        metrics.Content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["query"] = Query,
            ["time"] = atUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            ["timeout"] = "3s"
        });
        using var rules = new HttpRequestMessage(HttpMethod.Get, "api/v1/rules?type=alert&exclude_alerts=true&rule_group%5B%5D=goldsrcops-command-dispatch-liveness");
        var metricsTask = ReadAsync(metrics, ParseMetrics, cancellationToken);
        var rulesTask = ReadAsync(rules, ParseRules, cancellationToken);
        await Task.WhenAll(metricsTask, rulesTask).ConfigureAwait(false);
        return new CommandDispatchHealthSnapshot(await metricsTask.ConfigureAwait(false), await rulesTask.ConfigureAwait(false));
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

    private static IReadOnlyList<CommandDispatchMetricSample>? ParseMetrics(JsonElement data)
    {
        if (data.GetProperty("resultType").GetString() is not "vector" || data.GetProperty("result").GetArrayLength() > 48)
        {
            return null;
        }

        var result = new List<CommandDispatchMetricSample>();
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
            result.Add(new CommandDispatchMetricSample(RequiredString(sample.GetProperty("metric"), "service_instance_id"), RequiredString(sample.GetProperty("metric"), "gso_command_dispatch_field"),
                double.Parse(number, CultureInfo.InvariantCulture), DateTimeOffset.UnixEpoch.AddSeconds(seconds)));
        }

        return result;
    }

    private static IReadOnlyList<CommandDispatchRuleSample>? ParseRules(JsonElement data)
    {
        var groups = data.GetProperty("groups");
        if (data.TryGetProperty("groupNextToken", out _) || groups.GetArrayLength() != 1)
        {
            return null;
        }

        var group = groups[0];
        var rules = group.GetProperty("rules");
        if (rules.GetArrayLength() != 3)
        {
            return null;
        }

        var result = new List<CommandDispatchRuleSample>(3);
        foreach (var rule in rules.EnumerateArray())
        {
            if (RequiredString(rule, "type") is not "alerting")
            {
                return null;
            }

            var labels = rule.GetProperty("labels");
            result.Add(new CommandDispatchRuleSample(RequiredString(rule, "name"), RequiredString(group, "name"),
                RequiredString(labels, "component"), RequiredString(labels, "severity"), RequiredString(rule, "state"),
                RequiredString(rule, "health"), rule.GetProperty("lastEvaluation").GetDateTimeOffset()));
        }

        return result;
    }

    private static string RequiredString(JsonElement item, string name) =>
        item.GetProperty(name).GetString() is { Length: > 0 and <= 128 } value ? value : throw new JsonException("Invalid fixed observation field.");
}
