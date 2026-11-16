using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace GoldSrcOps.TelemetryAcceptance;

internal sealed class AgentProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task _stdout;
    private readonly Task _stderr;

    private AgentProcess(Process process)
    {
        _process = process;
        _stdout = DrainAsync(process.StandardOutput);
        _stderr = DrainAsync(process.StandardError);
    }

    public static AgentProcess Start(string agent, string scratch, Guid serverId, Uri proxy, bool delivery, params string[] command)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = scratch,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(agent);
        foreach (var argument in command)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var name in start.Environment.Keys.Where(static name =>
                     name.StartsWith("GameEventAgent", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            start.Environment.Remove(name);
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DOTNET_ENVIRONMENT"] = "Production",
            ["Logging__LogLevel__Default"] = "Warning",
            ["GameEventAgent__QueuePath"] = Path.Combine(scratch, "queue.db"),
            ["GameEventAgent__Spool__RootPath"] = Path.Combine(scratch, "spool"),
            ["GameEventAgent__Spool__Enabled"] = delivery ? "false" : "true",
            ["GameEventAgent__Delivery__Enabled"] = delivery ? "true" : "false",
            ["GameEventAgent__Delivery__ServerId"] = serverId.ToString("D"),
            ["GameEventAgent__Delivery__ApiBaseUrl"] = proxy.AbsoluteUri,
            ["GameEventAgent__Delivery__DispatchIntervalSeconds"] = "1",
            ["GameEventAgent__Delivery__LeaseSeconds"] = "5",
            ["GameEventAgent__Delivery__RequestTimeoutSeconds"] = "1",
            ["GameEventAgent__Delivery__RetryBaseSeconds"] = "1",
            ["GameEventAgent__Delivery__RetryMaximumSeconds"] = "2",
            ["GameEventAgent__Delivery__MaximumAttempts"] = "30",
            ["GameEventAgent__Delivery__MaximumEventAgeDays"] = "30",
            ["GameEventAgent__Delivery__BatchSize"] = "1",
            ["GameEventAgent__Delivery__OAuth__TokenEndpoint"] = new Uri(proxy, "token").AbsoluteUri,
            ["GameEventAgent__Delivery__OAuth__ClientId"] = "acceptance-agent",
            ["GameEventAgent__Delivery__OAuth__ClientSecretFile"] = Path.Combine(scratch, "oauth-secret"),
            ["GameEventAgent__Delivery__OAuth__Audience"] = AcceptanceApi.Audience,
            ["GameEventAgent__Delivery__OAuth__Scope"] = "ingest:game-events",
        };
        foreach (var pair in values)
        {
            start.Environment[pair.Key] = pair.Value;
        }

        return new AgentProcess(Process.Start(start) ?? throw new InvalidOperationException("Agent did not start."));
    }

    public async Task CompleteAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await _process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        await Task.WhenAll(_stdout, _stderr).ConfigureAwait(false);
        EntryPoint.Require(_process.ExitCode == 0, "Agent CLI command failed.");
    }

    public void RequireRunning() => EntryPoint.Require(!_process.HasExited, "Agent exited before acceptance completed.");

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            }

            await Task.WhenAll(_stdout, _stderr).ConfigureAwait(false);
        }
        finally
        {
            _process.Dispose();
        }
    }

    public static QueueIdentity? ReadQueue(string scratch)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(scratch, "queue.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.source_instance_id, a.next_sequence_number, q.event_id, q.sequence_number, q.payload_json
            FROM agent_state a CROSS JOIN game_event_outbox q;
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var identity = new QueueIdentity(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetInt64(3),
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData((byte[])reader[4])));
        EntryPoint.Require(!reader.Read(), "Expected exactly one queued event.");
        return identity;
    }

    public static (string Source, long NextSequence) ReadState(string scratch)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(scratch, "queue.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT source_instance_id, next_sequence_number FROM agent_state WHERE singleton_id = 1";
        using var reader = command.ExecuteReader();
        EntryPoint.Require(reader.Read(), "Agent source state disappeared.");
        return (reader.GetString(0), reader.GetInt64(1));
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        var count = 0;
        int length;
        while ((length = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            count += length;
            if (count > 1024 * 1024)
            {
                throw new InvalidOperationException("Agent diagnostic output exceeded bound: " + count.ToString(CultureInfo.InvariantCulture));
            }
        }
    }
}
