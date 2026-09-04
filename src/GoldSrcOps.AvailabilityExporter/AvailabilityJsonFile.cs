using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GoldSrcOps.Application.Availability;

namespace GoldSrcOps.AvailabilityExporter;

internal static class AvailabilityJsonFile
{
    private const int MaximumLineLength = 16 * 1024;
    private const int MaximumRecordCount = 1_000_000;

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static async Task WriteJsonLinesNewAsync(
        string path,
        IEnumerable<CanonicalAvailabilityResult> records,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(records);

        await WriteNewAsync(
            path,
            async stream =>
            {
                using var writer = new StreamWriter(
                    stream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    bufferSize: 16 * 1024,
                    leaveOpen: true)
                {
                    NewLine = "\n",
                };

                foreach (var record in records)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var line = JsonSerializer.Serialize(
                        CanonicalAvailabilityJsonRecord.FromDomain(record),
                        SerializerOptions);
                    await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
                }

                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public static Task WriteJsonNewAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken) =>
        WriteNewAsync(
            path,
            async stream =>
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    value,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);

    public static async Task<IReadOnlyList<CanonicalAvailabilityResult>> ReadJsonLinesAsync(
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var files = ResolveInputFiles(path);
        var records = new List<CanonicalAvailabilityResult>();

        foreach (var file in files)
        {
            await using var stream = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 16 * 1024,
                leaveOpen: false);

            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0 || line.Length > MaximumLineLength)
                {
                    throw new InvalidDataException("An availability evidence line has an invalid length.");
                }

                var record = JsonSerializer.Deserialize<CanonicalAvailabilityJsonRecord>(line, SerializerOptions)
                    ?? throw new InvalidDataException("An availability evidence record is null.");
                records.Add(record.ToDomain());

                if (records.Count > MaximumRecordCount)
                {
                    throw new InvalidDataException("The availability evidence input exceeded the record limit.");
                }
            }
        }

        return records;
    }

    private static async Task WriteNewAsync(
        string path,
        Func<FileStream, Task> write,
        CancellationToken cancellationToken)
    {
        var destination = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException("The output path has no parent directory.", nameof(path));
        Directory.CreateDirectory(directory);

        if (File.Exists(destination))
        {
            throw new IOException("The output file already exists; evidence files are create-only.");
        }

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await write(stream).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, destination, overwrite: false);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static string[] ResolveInputFiles(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath))
        {
            return [fullPath];
        }

        if (!Directory.Exists(fullPath))
        {
            throw new FileNotFoundException("The availability evidence input does not exist.");
        }

        var files = Directory.GetFiles(fullPath, "*.jsonl", SearchOption.TopDirectoryOnly);
        Array.Sort(files, StringComparer.Ordinal);

        if (files.Length == 0)
        {
            throw new InvalidDataException("The availability evidence directory contains no JSONL files.");
        }

        return files;
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            NumberHandling = JsonNumberHandling.Strict,
            PropertyNameCaseInsensitive = false,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = false,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }
}
