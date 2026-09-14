namespace GoldSrcOps.GameEventAgent;

internal static class GameEventSpoolFileSystem
{
    private const UnixFileMode OwnerDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public static void EnsureDirectories(GameEventSpoolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        EnsureDirectory(options.RootPath);
        EnsureDirectory(options.IncomingPath);
        EnsureDirectory(options.ProcessingPath);
        EnsureDirectory(options.AcceptedPath);
        EnsureDirectory(options.RejectedPath);
    }

    public static GameEventSpoolStatistics GetStatistics(GameEventSpoolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new GameEventSpoolStatistics(
            CountFiles(options.IncomingPath, "*.json"),
            CountFiles(options.ProcessingPath, "*.json"),
            CountFiles(options.AcceptedPath, "*.json"),
            CountFiles(options.RejectedPath, "*.json"),
            CountFiles(options.IncomingPath, "*.tmp"));
    }

    public static bool IsReparsePoint(string path) =>
        File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    private static void EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("A game-event spool directory must not be a reparse point.");
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, OwnerDirectoryMode);
        }
    }

    private static int CountFiles(string path, string pattern) =>
        Directory.Exists(path)
            ? Directory.EnumerateFiles(path, pattern, SearchOption.TopDirectoryOnly).Count()
            : 0;
}

internal sealed record GameEventSpoolStatistics(
    int Ready,
    int Processing,
    int Accepted,
    int Rejected,
    int Temporary);
