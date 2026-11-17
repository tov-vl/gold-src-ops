using System.Security.Cryptography;
using System.Text.Json;

namespace GoldSrcOps.TelemetryAcceptance;

internal static class AcceptanceFiles
{
    private static readonly string[] SpoolDirectories = ["incoming", "processing", "accepted", "rejected"];

    public static async Task PrepareAsync(string agent, string directory, Guid serverId, FaultProxy proxy, string secret, NativeInput[] inputs)
    {
        EntryPoint.Require(!Directory.Exists(directory), "Scenario directory must be fresh.");
        Directory.CreateDirectory(directory);
        var secretPath = Path.Combine(directory, "oauth-secret");
        await File.WriteAllTextAsync(secretPath, secret, CancellationToken.None).ConfigureAwait(false);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(secretPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var initialize = AgentProcess.Start(agent, directory, serverId, proxy.Address, delivery: false, "initialize-queue", "--fresh");
        await using (initialize.ConfigureAwait(false))
        {
            await initialize.CompleteAsync().ConfigureAwait(false);
        }

        var incoming = Path.Combine(directory, "spool", "incoming");
        Directory.CreateDirectory(incoming);
        foreach (var input in inputs)
        {
            await File.WriteAllBytesAsync(Path.Combine(incoming, input.RecordId.ToString("D") + ".json"), input.Bytes,
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    public static string[] SpoolFiles(string directory)
    {
        var directories = SpoolDirectories.Select(name => Path.Combine(directory, "spool", name)).Where(Directory.Exists);
        return [.. directories.SelectMany(Directory.GetFiles)];
    }

    public static WorkerCycles? ReadCycles(string directory)
    {
        try
        {
            using var stream = new FileStream(Path.Combine(directory, "queue.db.health.json"), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            EntryPoint.Require(stream.Length is > 0 and <= 16 * 1024, "Worker cycle record exceeds bound.");
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            EntryPoint.Require(root.GetProperty("schemaVersion").GetInt32() == 1 && root.GetProperty("importEnabled").GetBoolean() &&
                root.GetProperty("deliveryEnabled").GetBoolean(), "Both real worker loops must be enabled.");
            var started = root.GetProperty("startedAtUtc").GetDateTimeOffset();
            var import = root.GetProperty("import");
            var delivery = root.GetProperty("delivery");
            var complete = import.GetProperty("lastCompletedAtUtc");
            var delivered = delivery.GetProperty("lastCompletedAtUtc");
            var importTime = complete.ValueKind == JsonValueKind.Null ? (DateTimeOffset?)null : complete.GetDateTimeOffset();
            var deliveryTime = delivered.ValueKind == JsonValueKind.Null ? (DateTimeOffset?)null : delivered.GetDateTimeOffset();
            return new WorkerCycles(started, import.GetProperty("outcome").GetString(), delivery.GetProperty("outcome").GetString(),
                importTime, deliveryTime);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    public static NativeInput ReadInput(string directory, string name)
    {
        var bytes = File.ReadAllBytes(Path.Combine(directory, name + ".json"));
        EntryPoint.Require(bytes.Length is > 0 and <= 4096, "Native input exceeds bound.");
        using var document = JsonDocument.Parse(bytes);
        return new NativeInput(name, document.RootElement.GetProperty("recordId").GetGuid(), bytes,
            Convert.ToHexString(SHA256.HashData(bytes)), document.RootElement.GetProperty("event").Clone());
    }
}
