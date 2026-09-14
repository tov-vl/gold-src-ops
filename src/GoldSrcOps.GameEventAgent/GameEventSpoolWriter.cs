using System.Text.Json;

namespace GoldSrcOps.GameEventAgent;

internal sealed class GameEventSpoolWriter(
    GameEventSpoolOptions options,
    TimeProvider timeProvider)
{
    private const UnixFileMode OwnerFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public async Task<Guid> WriteAsync(
        GameEventSourceInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        GameEventSpoolFileSystem.EnsureDirectories(options);

        var normalized = GameEventContractRules.ValidateAndNormalize(
            input,
            timeProvider.GetUtcNow());
        var recordId = Guid.NewGuid();
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new GameEventSpoolEnvelope(
                GameEventSpoolContract.Version,
                recordId,
                normalized),
            GameEventJson.SerializerOptions);
        if (payload.Length > GameEventSpoolContract.MaximumRecordBytes)
        {
            throw new InvalidOperationException("The serialized game-event spool record exceeds 4 KiB.");
        }

        var temporaryPath = Path.Combine(options.IncomingPath, string.Concat(recordId.ToString("D"), ".tmp"));
        var readyPath = Path.Combine(
            options.IncomingPath,
            GameEventSpoolContract.GetReadyFileName(recordId));

        try
        {
            var streamOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 4 * 1024,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough
            };
            if (!OperatingSystem.IsWindows())
            {
                streamOptions.UnixCreateMode = OwnerFileMode;
            }

            await using (var stream = new FileStream(temporaryPath, streamOptions))
            {
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, readyPath, overwrite: false);
            return recordId;
        }
        catch
        {
            File.Delete(temporaryPath);
            throw;
        }
    }
}
