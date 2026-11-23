using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Npgsql;

namespace GoldSrcOps.TelemetryAcceptance;

internal sealed class PitrPostgres
{
    public const string ArchiveCommand = "archive_command=mkdir -p /tmp/pitr-wal && cp %p /tmp/pitr-wal/%f.tmp && mv /tmp/pitr-wal/%f.tmp /tmp/pitr-wal/%f";
    public const string Before = "before-late-ack";
    public const string After = "after-late-ack";
    private const string BasePath = "/tmp/pitr-base/base.tar.gz";
    private const string WalPath = "/tmp/pitr-wal.tar.gz";
    private const string MissingTargetMessage = "recovery ended before configured recovery target was reached";
    private const string MissingWalMarker = "PITR_MISSING_WAL_CONFIRMED";
    private const string RestoreScript = """
        export LC_ALL=C
        printf '%s  /tmp/base.tar.gz\n%s  /tmp/wal.tar.gz\n' "$3" "$4" | sha256sum -c -
        mkdir /tmp/pitr-data /tmp/pitr-archive
        tar -xzf /tmp/base.tar.gz -C /tmp/pitr-data
        tar -xzf /tmp/wal.tar.gz -C /tmp/pitr-archive
        chmod 0700 /tmp/pitr-data
        chown -R postgres:postgres /tmp/pitr-data
        touch /tmp/pitr-data/recovery.signal
        set -- "$1" "$2" gosu postgres postgres -D /tmp/pitr-data \
          -c 'listen_addresses=*' -c archive_mode=off -c hot_standby=off \
          -c 'restore_command=cp /tmp/pitr-archive/%f %p' \
          -c "recovery_target_name=$1" -c recovery_target_action=promote
        missing="$2"
        shift 2
        if [ -z "$missing" ]; then
          exec "$@"
        fi
        test -f "/tmp/pitr-archive/$missing"
        rm -- "/tmp/pitr-archive/$missing"
        if "$@" > /tmp/pitr-negative.log 2>&1; then
          exit 1
        fi
        grep -F 'recovery ended before configured recovery target was reached' /tmp/pitr-negative.log
        echo PITR_MISSING_WAL_CONFIRMED
        """;

    private readonly byte[] _baseBackup;
    private readonly byte[] _wal;
    private readonly string _baseSha256;
    private readonly string _walSha256;
    private readonly string _lateSegment;
    private readonly long _archiveDelayMilliseconds;

    private PitrPostgres(byte[] baseBackup, byte[] wal, string lateSegment, long archiveDelayMilliseconds)
    {
        _baseBackup = baseBackup;
        _wal = wal;
        _baseSha256 = Convert.ToHexStringLower(SHA256.HashData(baseBackup));
        _walSha256 = Convert.ToHexStringLower(SHA256.HashData(wal));
        _lateSegment = lateSegment;
        _archiveDelayMilliseconds = archiveDelayMilliseconds;
    }

    public static async Task<byte[]> BackupAsync(IContainer source)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = deadline.Token;
        await ExecAsync(source,
            ["pg_basebackup", "-h", "/var/run/postgresql", "-U", AcceptancePostgres.Username, "-D", "/tmp/pitr-base",
                "--format=tar", "--gzip", "--wal-method=none", "--checkpoint=fast"], token).ConfigureAwait(false);
        var backup = await ReadArchiveAsync(source, BasePath, token).ConfigureAwait(false);
        await MarkAndArchiveAsync(source, Before, token).ConfigureAwait(false);
        return backup;
    }

    public static async Task<PitrPostgres> CaptureAsync(IContainer source, byte[] baseBackup)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = deadline.Token;
        var timer = Stopwatch.StartNew();
        var segment = await MarkAndArchiveAsync(source, After, token).ConfigureAwait(false);
        var archiveDelay = timer.ElapsedMilliseconds;
        await ExecAsync(source, ["tar", "-czf", WalPath, "-C", "/tmp/pitr-wal", "."], token).ConfigureAwait(false);
        var wal = await ReadArchiveAsync(source, WalPath, token).ConfigureAwait(false);
        return new PitrPostgres(baseBackup, wal, segment, archiveDelay);
    }

    public IContainer BuildRestore(string target, bool missingWal = false)
    {
        var wait = missingWal
            ? Wait.ForUnixContainer().UntilMessageIsLogged(MissingWalMarker, options => options.WithMode(WaitStrategyMode.OneShot))
            : Wait.ForUnixContainer().UntilCommandIsCompleted("pg_isready", "-h", "127.0.0.1");
        return new ContainerBuilder(AcceptancePostgres.Image)
            .WithEntrypoint("/bin/sh", "-ec")
            .WithCommand(RestoreScript, "telemetry-pitr", target, missingWal ? _lateSegment : string.Empty, _baseSha256, _walSha256)
            .WithResourceMapping(_baseBackup, "/tmp/base.tar.gz")
            .WithResourceMapping(_wal, "/tmp/wal.tar.gz")
            .WithPortBinding(5432, assignRandomHostPort: true)
            .WithCreateParameterModifier(AcceptancePostgres.BindLoopback)
            .WithWaitStrategy(wait)
            .Build();
    }

    public static async Task<string> ConfirmTargetAsync(IContainer restored, string target, string originalConnectionString, CancellationToken token)
    {
        AcceptancePostgres.RequireLocal(restored);
        var (_, stderr) = await restored.GetLogsAsync(ct: token).ConfigureAwait(false);
        EntryPoint.Require(stderr.Contains("recovery stopping at restore point \"" + target + "\"", StringComparison.Ordinal),
            "PostgreSQL did not stop at the requested restore point.");
        var original = new NpgsqlConnectionStringBuilder(originalConnectionString)
        {
            Host = restored.Hostname,
            Port = restored.GetMappedPublicPort(5432),
        };
        await using var connection = new NpgsqlConnection(original.ConnectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT pg_is_in_recovery()", connection);
        EntryPoint.Require(await command.ExecuteScalarAsync(token).ConfigureAwait(false) is false, "Restored target did not become writable.");
        return original.ConnectionString;
    }

    public async Task ConfirmMissingWalAsync(CancellationToken token)
    {
        var missing = BuildRestore(After, missingWal: true);
        await using var lifetime = missing.ConfigureAwait(false);
        await missing.StartAsync(token).ConfigureAwait(false);
        var (stdout, _) = await missing.GetLogsAsync(ct: token).ConfigureAwait(false);
        EntryPoint.Require(await missing.GetExitCodeAsync(token).ConfigureAwait(false) == 0 &&
            stdout.Contains(MissingTargetMessage, StringComparison.Ordinal), "Missing WAL did not produce the expected recovery failure.");
    }

    public object Evidence(long earlyMilliseconds, long lateMilliseconds) => new
    {
        freshContainer = true,
        baseBytes = _baseBackup.Length,
        baseSha256 = _baseSha256,
        walBytes = _wal.Length,
        walSha256 = _walSha256,
        earlyTarget = Before,
        lateTarget = After,
        rowsAtEarlyTarget = 1,
        missingWalRejected = true,
        earlyRestoreMilliseconds = earlyMilliseconds,
        lateRestoreMilliseconds = lateMilliseconds,
        forcedArchiveMilliseconds = _archiveDelayMilliseconds,
    };

    private static async Task<string> MarkAndArchiveAsync(IContainer source, string target, CancellationToken token)
    {
        var segment = await SqlAsync(source, "SELECT pg_walfile_name(pg_create_restore_point('" + target + "'))", token).ConfigureAwait(false);
        EntryPoint.Require(segment.Length == 24 && segment.All(Uri.IsHexDigit), "Unexpected synthetic WAL segment name.");
        await SqlAsync(source, "SELECT pg_switch_wal()", token).ConfigureAwait(false);
        while ((await source.ExecAsync(["test", "-s", "/tmp/pitr-wal/" + segment], token).ConfigureAwait(false)).ExitCode != 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200), token).ConfigureAwait(false);
        }

        return segment;
    }

    private static Task<string> SqlAsync(IContainer source, string sql, CancellationToken token) =>
        ExecAsync(source, ["psql", "-XAt", "-v", "ON_ERROR_STOP=1", "-U", AcceptancePostgres.Username,
            "-d", AcceptancePostgres.DatabaseName, "-c", sql], token);

    private static async Task<string> ExecAsync(IContainer source, string[] command, CancellationToken token)
    {
        var result = await source.ExecAsync(command, token).ConfigureAwait(false);
        EntryPoint.Require(result.ExitCode == 0, string.Create(CultureInfo.InvariantCulture,
            $"Synthetic PostgreSQL {command[0]} failed ({result.ExitCode}): {result.Stderr.Trim()}"));
        return result.Stdout.Trim();
    }

    private static async Task<byte[]> ReadArchiveAsync(IContainer source, string path, CancellationToken token)
    {
        var count = await ExecAsync(source, ["stat", "-c", "%s", path], token).ConfigureAwait(false);
        EntryPoint.Require(int.TryParse(count, CultureInfo.InvariantCulture, out var expected) && expected is > 0 and <= 32 * 1024 * 1024,
            "Synthetic PostgreSQL archive exceeds its size bound.");
        var bytes = await source.ReadFileAsync(path, token).ConfigureAwait(false);
        EntryPoint.Require(bytes.Length == expected, "Synthetic PostgreSQL archive size changed during transfer.");
        return bytes;
    }
}
