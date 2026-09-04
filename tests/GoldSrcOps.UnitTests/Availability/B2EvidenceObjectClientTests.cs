using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using GoldSrcOps.AvailabilityExporter;
using Moq;

namespace GoldSrcOps.UnitTests.Availability;

public sealed class B2EvidenceObjectClientTests
{
    [Fact]
    public async Task UploadAsync_sends_a_single_attempt_content_addressed_put()
    {
        var amazonClient = new Mock<IAmazonS3>(MockBehavior.Strict);
        PutObjectRequest? capturedRequest = null;
        amazonClient
            .Setup(client => client.PutObjectAsync(
                It.IsAny<PutObjectRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<PutObjectRequest, CancellationToken>((request, _) => capturedRequest = request)
            .ReturnsAsync(new PutObjectResponse { HttpStatusCode = HttpStatusCode.OK });
        using var content = new MemoryStream("evidence"u8.ToArray());
        using var client = new B2EvidenceObjectClient(amazonClient.Object, "evidence-bucket");

        await client.UploadAsync(
            "availability/v1/segments/sha256/ab/abcdef.jsonl",
            content,
            content.Length,
            "abcdef",
            CancellationToken.None);

        capturedRequest.Should().NotBeNull();
        capturedRequest!.IfNoneMatch.Should().BeNull();
        capturedRequest.AutoCloseStream.Should().BeFalse();
        capturedRequest.BucketName.Should().Be("evidence-bucket");
        capturedRequest.ContentType.Should().Be("application/x-ndjson");
        capturedRequest.DisableDefaultChecksumValidation.Should().BeTrue();
        capturedRequest.Headers.ContentLength.Should().Be(content.Length);
        capturedRequest.Metadata["sha256"].Should().Be("abcdef");
        capturedRequest.UseChunkEncoding.Should().BeFalse();
        amazonClient.VerifyAll();
    }

    [Fact]
    public async Task ExistsAsync_returns_true_when_metadata_is_found()
    {
        var amazonClient = new Mock<IAmazonS3>(MockBehavior.Strict);
        amazonClient
            .Setup(client => client.GetObjectMetadataAsync(
                It.IsAny<GetObjectMetadataRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetObjectMetadataResponse { HttpStatusCode = HttpStatusCode.OK });
        using var client = new B2EvidenceObjectClient(amazonClient.Object, "evidence-bucket");

        var exists = await client.ExistsAsync(
            "availability/v1/segments/sha256/ab/abcdef.jsonl",
            CancellationToken.None);

        exists.Should().BeTrue();
        amazonClient.VerifyAll();
    }

    [Fact]
    public async Task ExistsAsync_returns_false_when_metadata_is_not_found()
    {
        var amazonClient = new Mock<IAmazonS3>(MockBehavior.Strict);
        amazonClient
            .Setup(client => client.GetObjectMetadataAsync(
                It.IsAny<GetObjectMetadataRequest>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("not found")
            {
                StatusCode = HttpStatusCode.NotFound,
            });
        using var client = new B2EvidenceObjectClient(amazonClient.Object, "evidence-bucket");

        var exists = await client.ExistsAsync(
            "availability/v1/segments/sha256/ab/abcdef.jsonl",
            CancellationToken.None);

        exists.Should().BeFalse();
        amazonClient.VerifyAll();
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, 400)]
    [InlineData(HttpStatusCode.Unauthorized, 401)]
    [InlineData(HttpStatusCode.Forbidden, 403)]
    [InlineData(HttpStatusCode.NotFound, 404)]
    public async Task UploadAsync_reports_only_the_safe_status_for_conclusive_rejections(
        HttpStatusCode statusCode,
        int expectedStatusCode)
    {
        var amazonClient = new Mock<IAmazonS3>(MockBehavior.Strict);
        amazonClient
            .Setup(client => client.PutObjectAsync(
                It.IsAny<PutObjectRequest>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("provider-specific detail: chunked transfer encoding")
            {
                ErrorCode = "InvalidArgument",
                StatusCode = statusCode,
            });
        using var content = new MemoryStream("evidence"u8.ToArray());
        using var client = new B2EvidenceObjectClient(amazonClient.Object, "evidence-bucket");

        Func<Task> upload = () => client.UploadAsync(
            "availability/v1/segments/sha256/ab/abcdef.jsonl",
            content,
            content.Length,
            "abcdef",
            CancellationToken.None);

        var exception = await upload.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Be(
            $"The evidence archive rejected the upload request with HTTP {expectedStatusCode} (InvalidArgument; category=chunked-upload).");
        exception.Which.Message.Should().NotContain("provider-specific detail");
    }

    [Theory]
    [InlineData("InvalidArgument: bucket=evidence-bucket")]
    [InlineData("PotentialSensitiveIdentifier123")]
    public async Task UploadAsync_does_not_report_an_untrusted_provider_error_code(string errorCode)
    {
        var amazonClient = new Mock<IAmazonS3>(MockBehavior.Strict);
        amazonClient
            .Setup(client => client.PutObjectAsync(
                It.IsAny<PutObjectRequest>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("provider-specific detail")
            {
                ErrorCode = errorCode,
                StatusCode = HttpStatusCode.BadRequest,
            });
        using var content = new MemoryStream("evidence"u8.ToArray());
        using var client = new B2EvidenceObjectClient(amazonClient.Object, "evidence-bucket");

        Func<Task> upload = () => client.UploadAsync(
            "availability/v1/segments/sha256/ab/abcdef.jsonl",
            content,
            content.Length,
            "abcdef",
            CancellationToken.None);

        var exception = await upload.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Be(
            "The evidence archive rejected the upload request with HTTP 400.");
    }

    [Fact]
    public void CreateClientConfiguration_disables_automatic_retries()
    {
        var settings = B2EvidenceStorageSettings.Create(
            "https://s3.eu-central-003.backblazeb2.com",
            "eu-central-003",
            "evidence-bucket",
            "key-id",
            "application-key");

        var configuration = B2EvidenceObjectClient.CreateClientConfiguration(settings);

        configuration.ServiceURL.Should().Be("https://s3.eu-central-003.backblazeb2.com/");
        configuration.AuthenticationRegion.Should().Be("eu-central-003");
        configuration.ForcePathStyle.Should().BeTrue();
        configuration.MaxErrorRetry.Should().Be(0);
        configuration.RequestChecksumCalculation.Should().Be(RequestChecksumCalculation.WHEN_REQUIRED);
        configuration.ResponseChecksumValidation.Should().Be(ResponseChecksumValidation.WHEN_REQUIRED);
        configuration.Timeout.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData("http://s3.eu-central-003.backblazeb2.com")]
    [InlineData("https://s3.other-region.backblazeb2.com")]
    [InlineData("https://user@s3.eu-central-003.backblazeb2.com")]
    [InlineData("https://s3.eu-central-003.backblazeb2.com/path")]
    public void Settings_reject_an_untrusted_endpoint(string endpoint)
    {
        Action create = () => B2EvidenceStorageSettings.Create(
            endpoint,
            "eu-central-003",
            "evidence-bucket",
            "key-id",
            "application-key");

        create.Should().Throw<InvalidOperationException>();
    }
}
