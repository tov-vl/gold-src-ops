using System.Security.Cryptography;
using AwesomeAssertions;
using GoldSrcOps.Application.Availability;
using GoldSrcOps.AvailabilityExporter;

namespace GoldSrcOps.UnitTests.Availability;

public sealed class EvidenceArchiveTests
{
    [Fact]
    public async Task UploadNewAsync_uses_a_content_addressed_object_key()
    {
        var directory = CreateTemporaryDirectory();
        var inputPath = Path.Combine(directory, "segment.jsonl");
        var client = new RecordingObjectClient();

        try
        {
            await AvailabilityJsonFile.WriteJsonLinesNewAsync(
                inputPath,
                [CreateRecord()],
                CancellationToken.None);
            var expectedBytes = await File.ReadAllBytesAsync(inputPath);
            var expectedSha256 = Convert.ToHexStringLower(SHA256.HashData(expectedBytes));
            var archive = new EvidenceArchive(client, client);

            var receipt = await archive.UploadNewAsync(inputPath, CancellationToken.None);

            receipt.Sha256.Should().Be(expectedSha256);
            receipt.ContentLength.Should().Be(expectedBytes.Length);
            receipt.ObjectKey.Should().Be(
                $"availability/v1/segments/sha256/{expectedSha256[..2]}/{expectedSha256}.jsonl");
            client.UploadedBytes.Should().Equal(expectedBytes);
            client.UploadedSha256.Should().Be(expectedSha256);
            client.UploadedObjectKey.Should().Be(receipt.ObjectKey);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task UploadNewAsync_does_not_upload_an_existing_content_addressed_object()
    {
        var directory = CreateTemporaryDirectory();
        var inputPath = Path.Combine(directory, "segment.jsonl");
        var client = new RecordingObjectClient { ExistingObject = true };

        try
        {
            await AvailabilityJsonFile.WriteJsonLinesNewAsync(
                inputPath,
                [CreateRecord()],
                CancellationToken.None);
            var archive = new EvidenceArchive(client, client);

            Func<Task> upload = () => archive.UploadNewAsync(inputPath, CancellationToken.None);

            await upload.Should().ThrowAsync<EvidenceObjectAlreadyExistsException>()
                .WithMessage("*no upload was attempted*");
            client.UploadCount.Should().Be(0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadVerifiedNewAsync_verifies_digest_and_canonical_records()
    {
        var directory = CreateTemporaryDirectory();
        var sourcePath = Path.Combine(directory, "source.jsonl");
        var destinationPath = Path.Combine(directory, "restored.jsonl");

        try
        {
            await AvailabilityJsonFile.WriteJsonLinesNewAsync(
                sourcePath,
                [CreateRecord()],
                CancellationToken.None);
            var bytes = await File.ReadAllBytesAsync(sourcePath);
            var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var archive = new EvidenceArchive(new RecordingObjectClient(bytes));

            var receipt = await archive.DownloadVerifiedNewAsync(
                sha256,
                destinationPath,
                CancellationToken.None);

            receipt.Sha256.Should().Be(sha256);
            receipt.ContentLength.Should().Be(bytes.Length);
            receipt.RecordCount.Should().Be(1);
            (await File.ReadAllBytesAsync(destinationPath)).Should().Equal(bytes);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadVerifiedNewAsync_removes_partial_output_when_digest_does_not_match()
    {
        var directory = CreateTemporaryDirectory();
        var destinationPath = Path.Combine(directory, "restored.jsonl");
        var archive = new EvidenceArchive(new RecordingObjectClient("not evidence"u8.ToArray()));

        try
        {
            Func<Task> download = () => archive.DownloadVerifiedNewAsync(
                new string('0', 64),
                destinationPath,
                CancellationToken.None);

            await download.Should().ThrowAsync<InvalidDataException>()
                .WithMessage("*SHA-256*");
            File.Exists(destinationPath).Should().BeFalse();
            Directory.GetFiles(directory).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Recovery_rehearsal_matches_the_saved_deterministic_report()
    {
        var directory = CreateTemporaryDirectory();
        var sourcePath = Path.Combine(directory, "source.jsonl");
        var downloadPath = Path.Combine(directory, "downloaded.jsonl");
        var expectedReportPath = Path.Combine(directory, "expected.json");
        var actualReportPath = Path.Combine(directory, "actual.json");

        try
        {
            await AvailabilityJsonFile.WriteJsonLinesNewAsync(
                sourcePath,
                [CreateRecord()],
                CancellationToken.None);
            var bytes = await File.ReadAllBytesAsync(sourcePath);
            var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var request = new AvailabilityEvaluationRequest(
                new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 9, 4, 13, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 9, 4, 13, 5, 0, TimeSpan.Zero),
                "v2-4-shadow-001",
                "region-a",
                TimeSpan.FromMinutes(5),
                0.995m);
            var expectedReport = AvailabilityEvaluator.Evaluate([CreateRecord()], request);
            await AvailabilityJsonFile.WriteJsonNewAsync(
                expectedReportPath,
                expectedReport,
                CancellationToken.None);
            var archive = new EvidenceArchive(new RecordingObjectClient(bytes));
            var rehearsal = new AvailabilityRecoveryRehearsal(archive);

            var result = await rehearsal.RunAsync(
                sha256,
                downloadPath,
                expectedReportPath,
                actualReportPath,
                request,
                CancellationToken.None);

            result.Report.Should().Be(expectedReport);
            (await AvailabilityJsonFile.ReadJsonAsync<AvailabilityEvaluationReport>(
                actualReportPath,
                CancellationToken.None)).Should().Be(expectedReport);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "GoldSrcOps.UnitTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static CanonicalAvailabilityResult CreateRecord()
    {
        var scheduledAtUtc = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
        return new CanonicalAvailabilityResult(
            scheduledAtUtc,
            scheduledAtUtc.AddSeconds(10),
            scheduledAtUtc.AddSeconds(10).AddMilliseconds(250),
            "v2-4-shadow-001",
            "region-a",
            AvailabilityProbeRole.Primary,
            "sha256:fixture",
            AvailabilityOutcome.Good,
            200,
            250);
    }

    private sealed class RecordingObjectClient(byte[]? downloadBytes = null)
        : IEvidenceObjectReader, IEvidenceObjectWriter
    {
        public bool ExistingObject { get; init; }

        public int UploadCount { get; private set; }

        public byte[] UploadedBytes { get; private set; } = [];

        public string? UploadedObjectKey { get; private set; }

        public string? UploadedSha256 { get; private set; }

        public Task<bool> ExistsAsync(
            string objectKey,
            CancellationToken cancellationToken)
        {
            objectKey.Should().StartWith("availability/v1/segments/sha256/");
            return Task.FromResult(ExistingObject);
        }

        public async Task UploadAsync(
            string objectKey,
            Stream content,
            long contentLength,
            string sha256,
            CancellationToken cancellationToken)
        {
            UploadCount++;
            using var copy = new MemoryStream();
            await content.CopyToAsync(copy, cancellationToken);
            UploadedBytes = copy.ToArray();
            UploadedBytes.LongLength.Should().Be(contentLength);
            UploadedObjectKey = objectKey;
            UploadedSha256 = sha256;
        }

        public async Task DownloadAsync(
            string objectKey,
            Stream destination,
            long maximumBytes,
            CancellationToken cancellationToken)
        {
            objectKey.Should().StartWith("availability/v1/segments/sha256/");
            downloadBytes.Should().NotBeNull();
            downloadBytes!.LongLength.Should().BeLessThanOrEqualTo(maximumBytes);
            await destination.WriteAsync(downloadBytes, cancellationToken);
        }
    }
}
