using System.Security.Cryptography;

namespace GoldSrcOps.AvailabilityExporter;

internal interface IEvidenceObjectReader
{
    Task<bool> ExistsAsync(
        string objectKey,
        CancellationToken cancellationToken);

    Task DownloadAsync(
        string objectKey,
        Stream destination,
        long maximumBytes,
        CancellationToken cancellationToken);
}

internal interface IEvidenceObjectWriter
{
    Task UploadAsync(
        string objectKey,
        Stream content,
        long contentLength,
        string sha256,
        CancellationToken cancellationToken);
}

internal sealed record EvidenceArchiveReceipt(string Sha256, long ContentLength, string ObjectKey);

internal sealed record EvidenceDownloadReceipt(string Sha256, long ContentLength, int RecordCount);

internal sealed class EvidenceArchive(
    IEvidenceObjectReader objectReader,
    IEvidenceObjectWriter? objectWriter = null)
{
    internal const long MaximumObjectBytes = 64L * 1024 * 1024;

    private const string ObjectPrefix = "availability/v1/segments/sha256";

    public async Task<EvidenceArchiveReceipt> UploadNewAsync(
        string inputPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);

        var sourcePath = Path.GetFullPath(inputPath);
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        EnsureValidLength(source.Length);
        var records = await AvailabilityJsonFile.ReadJsonLinesAsync(
            sourcePath,
            cancellationToken).ConfigureAwait(false);
        if (records.Count == 0)
        {
            throw new InvalidDataException("The availability evidence segment contains no records.");
        }

        var sha256 = await ComputeSha256Async(source, cancellationToken).ConfigureAwait(false);
        var objectKey = BuildObjectKey(sha256);
        if (await objectReader.ExistsAsync(objectKey, cancellationToken).ConfigureAwait(false))
        {
            throw new EvidenceObjectAlreadyExistsException();
        }

        var writer = objectWriter
            ?? throw new InvalidOperationException("The evidence archive writer is not configured.");
        source.Position = 0;

        await writer.UploadAsync(
            objectKey,
            source,
            source.Length,
            sha256,
            cancellationToken).ConfigureAwait(false);

        return new EvidenceArchiveReceipt(sha256, source.Length, objectKey);
    }

    public async Task<EvidenceDownloadReceipt> DownloadVerifiedNewAsync(
        string expectedSha256,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var sha256 = NormalizeSha256(expectedSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var destination = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException("The output path has no parent directory.", nameof(outputPath));
        Directory.CreateDirectory(directory);

        if (File.Exists(destination))
        {
            throw new IOException("The download output already exists; rehearsal files are create-only.");
        }

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");

        try
        {
            long contentLength;
            await using (var temporary = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await objectReader.DownloadAsync(
                    BuildObjectKey(sha256),
                    temporary,
                    MaximumObjectBytes,
                    cancellationToken).ConfigureAwait(false);
                contentLength = temporary.Length;
                EnsureValidLength(contentLength);

                var actualSha256 = await ComputeSha256Async(temporary, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(actualSha256, sha256, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The downloaded evidence failed SHA-256 verification.");
                }

                await temporary.FlushAsync(cancellationToken).ConfigureAwait(false);
                temporary.Flush(flushToDisk: true);
            }

            var records = await AvailabilityJsonFile.ReadJsonLinesAsync(
                temporaryPath,
                cancellationToken).ConfigureAwait(false);
            if (records.Count == 0)
            {
                throw new InvalidDataException("The downloaded evidence segment contains no records.");
            }

            File.Move(temporaryPath, destination, overwrite: false);
            return new EvidenceDownloadReceipt(sha256, contentLength, records.Count);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    internal static string BuildObjectKey(string sha256)
    {
        var normalized = NormalizeSha256(sha256);
        return $"{ObjectPrefix}/{normalized[..2]}/{normalized}.jsonl";
    }

    internal static string NormalizeSha256(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("The SHA-256 value must contain exactly 64 hexadecimal characters.", nameof(value));
        }

        return value.ToLowerInvariant();
    }

    private static async Task<string> ComputeSha256Async(
        Stream stream,
        CancellationToken cancellationToken)
    {
        stream.Position = 0;
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static void EnsureValidLength(long length)
    {
        if (length <= 0 || length > MaximumObjectBytes)
        {
            throw new InvalidDataException(
                $"The evidence object must be between 1 and {MaximumObjectBytes} bytes.");
        }
    }
}
