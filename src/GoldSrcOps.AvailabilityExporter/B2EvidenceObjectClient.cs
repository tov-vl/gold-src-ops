using System.Buffers;
using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace GoldSrcOps.AvailabilityExporter;

internal sealed class B2EvidenceStorageSettings
{
    private B2EvidenceStorageSettings(
        Uri endpoint,
        string region,
        string bucketName,
        string keyId,
        string applicationKey)
    {
        Endpoint = endpoint;
        Region = region;
        BucketName = bucketName;
        KeyId = keyId;
        ApplicationKey = applicationKey;
    }

    public Uri Endpoint { get; }

    public string Region { get; }

    public string BucketName { get; }

    public string KeyId { get; }

    public string ApplicationKey { get; }

    public static B2EvidenceStorageSettings Create(
        string endpointText,
        string region,
        string bucketName,
        string keyId,
        string applicationKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointText);
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        ArgumentException.ThrowIfNullOrWhiteSpace(bucketName);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationKey);

        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint) ||
            !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
            !endpoint.IsDefaultPort ||
            !string.Equals(endpoint.AbsolutePath, "/", StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new InvalidOperationException("The B2 S3 endpoint must be an HTTPS origin URL.");
        }

        if (!IsValidRegion(region) ||
            !string.Equals(
                endpoint.Host,
                $"s3.{region}.backblazeb2.com",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The B2 S3 endpoint does not match the configured region.");
        }

        if (!IsValidBucketName(bucketName))
        {
            throw new InvalidOperationException("The B2 bucket name is invalid.");
        }

        return new B2EvidenceStorageSettings(
            endpoint,
            region,
            bucketName,
            keyId,
            applicationKey);
    }

    private static bool IsValidRegion(string value) =>
        value.Length is >= 3 and <= 32 &&
        value[0] is >= 'a' and <= 'z' &&
        value[^1] is >= '0' and <= '9' &&
        value.All(character =>
            character is >= 'a' and <= 'z' ||
            character is >= '0' and <= '9' ||
            character == '-');

    private static bool IsValidBucketName(string value) =>
        value.Length is >= 6 and <= 63 &&
        IsAsciiLetterOrDigit(value[0]) &&
        IsAsciiLetterOrDigit(value[^1]) &&
        value.All(character => IsAsciiLetterOrDigit(character) || character == '-');

    private static bool IsAsciiLetterOrDigit(char value) =>
        value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
}

internal sealed class B2EvidenceObjectClient : IEvidenceObjectReader, IEvidenceObjectWriter, IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly HashSet<string> SafeErrorCodes = new(StringComparer.Ordinal)
    {
        "AccessDenied",
        "InvalidAccessKeyId",
        "InvalidArgument",
        "InvalidRequest",
        "NoSuchBucket",
        "NoSuchKey",
        "NotFound",
        "RequestTimeTooSkewed",
        "SignatureDoesNotMatch",
    };

    private readonly IAmazonS3 client;
    private readonly string bucketName;
    private readonly bool ownsClient;

    internal B2EvidenceObjectClient(IAmazonS3 client, string bucketName, bool ownsClient = false)
    {
        this.client = client;
        this.bucketName = bucketName;
        this.ownsClient = ownsClient;
    }

    public static B2EvidenceObjectClient Create(B2EvidenceStorageSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var credentials = new BasicAWSCredentials(settings.KeyId, settings.ApplicationKey);
        var amazonClient = new AmazonS3Client(credentials, CreateClientConfiguration(settings));
        return new B2EvidenceObjectClient(amazonClient, settings.BucketName, ownsClient: true);
    }

    public async Task UploadAsync(
        string objectKey,
        Stream content,
        long contentLength,
        string sha256,
        CancellationToken cancellationToken)
    {
        var request = new PutObjectRequest
        {
            AutoCloseStream = false,
            BucketName = bucketName,
            ContentType = "application/x-ndjson",
            DisableDefaultChecksumValidation = true,
            InputStream = content,
            Key = objectKey,
            UseChunkEncoding = false,
        };
        request.Headers.ContentLength = contentLength;
        request.Metadata.Add("sha256", sha256);

        try
        {
            var response = await client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.HttpStatusCode != HttpStatusCode.OK)
            {
                throw new EvidenceUploadOutcomeUnknownException();
            }
        }
        catch (AmazonS3Exception exception) when (IsConclusiveRejection(exception.StatusCode))
        {
            throw new InvalidOperationException(
                CreateConclusiveRejectionMessage(exception, "upload"),
                exception);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (EvidenceUploadOutcomeUnknownException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new EvidenceUploadOutcomeUnknownException(exception);
        }
    }

    public async Task<bool> ExistsAsync(
        string objectKey,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.GetObjectMetadataAsync(
                new GetObjectMetadataRequest
                {
                    BucketName = bucketName,
                    Key = objectKey,
                },
                cancellationToken).ConfigureAwait(false);

            if (response.HttpStatusCode != HttpStatusCode.OK)
            {
                throw new InvalidDataException(
                    "The evidence archive returned an unexpected metadata response.");
            }

            return true;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
        catch (AmazonS3Exception exception) when (IsConclusiveRejection(exception.StatusCode))
        {
            throw new InvalidOperationException(
                CreateConclusiveRejectionMessage(exception, "metadata lookup"),
                exception);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new IOException(
                "The evidence archive metadata lookup failed; no upload was attempted.",
                exception);
        }
    }

    public async Task DownloadAsync(
        string objectKey,
        Stream destination,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetObjectAsync(
                new GetObjectRequest
                {
                    BucketName = bucketName,
                    Key = objectKey,
                },
                cancellationToken).ConfigureAwait(false);

            if (response.HttpStatusCode != HttpStatusCode.OK)
            {
                throw new InvalidDataException("The evidence archive returned an unexpected download response.");
            }

            await CopyBoundedAsync(
                response.ResponseStream,
                destination,
                maximumBytes,
                cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException("The requested evidence object was not found.", exception);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new IOException("The evidence archive download failed.", exception);
        }
    }

    private static async Task CopyBoundedAsync(
        Stream source,
        Stream destination,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long totalBytes = 0;
            int bytesRead;
            while ((bytesRead = await source.ReadAsync(
                buffer.AsMemory(0, buffer.Length),
                cancellationToken).ConfigureAwait(false)) > 0)
            {
                totalBytes = checked(totalBytes + bytesRead);
                if (totalBytes > maximumBytes)
                {
                    throw new InvalidDataException("The downloaded evidence exceeded the size limit.");
                }

                await destination.WriteAsync(
                    buffer.AsMemory(0, bytesRead),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Dispose()
    {
        if (ownsClient)
        {
            client.Dispose();
        }
    }

    internal static AmazonS3Config CreateClientConfiguration(B2EvidenceStorageSettings settings) =>
        new()
        {
            AuthenticationRegion = settings.Region,
            ForcePathStyle = true,
            MaxErrorRetry = 0,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            ServiceURL = settings.Endpoint.AbsoluteUri,
            Timeout = RequestTimeout,
        };

    private static bool IsConclusiveRejection(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.BadRequest or
            HttpStatusCode.Unauthorized or
            HttpStatusCode.Forbidden or
            HttpStatusCode.NotFound;

    private static string CreateConclusiveRejectionMessage(
        AmazonS3Exception exception,
        string operation)
    {
        var status = (int)exception.StatusCode;
        var errorCode = exception.ErrorCode;
        var category = ClassifyRejection(exception);
        var categorySuffix = category is null ? string.Empty : $"; category={category}";
        if (errorCode is not null && SafeErrorCodes.Contains(errorCode))
        {
            return FormattableString.Invariant(
                $"The evidence archive rejected the {operation} request with HTTP {status} ({errorCode}{categorySuffix}).");
        }

        return FormattableString.Invariant(
            $"The evidence archive rejected the {operation} request with HTTP {status}{categorySuffix}.");
    }

    private static string? ClassifyRejection(AmazonS3Exception exception)
    {
        var detail = string.Concat(exception.Message, "\n", exception.ResponseBody);
        if (detail.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            return "chunked-upload";
        }

        if (detail.Contains("checksum", StringComparison.OrdinalIgnoreCase))
        {
            return "checksum";
        }

        if (detail.Contains("content-length", StringComparison.OrdinalIgnoreCase))
        {
            return "content-length";
        }

        if (detail.Contains("x-amz-content-sha256", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("payload signing", StringComparison.OrdinalIgnoreCase))
        {
            return "payload-signing";
        }

        if (detail.Contains("x-amz-meta", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("metadata", StringComparison.OrdinalIgnoreCase))
        {
            return "metadata";
        }

        if (detail.Contains("header", StringComparison.OrdinalIgnoreCase) &&
            (detail.Contains("unsupported", StringComparison.OrdinalIgnoreCase) ||
             detail.Contains("not supported", StringComparison.OrdinalIgnoreCase)))
        {
            return "unsupported-header";
        }

        return null;
    }
}

internal sealed class EvidenceObjectAlreadyExistsException()
    : IOException("The evidence object already exists; no upload was attempted.");

internal sealed class EvidenceUploadOutcomeUnknownException : IOException
{
    private const string SafeMessage =
        "The evidence upload outcome is unknown. Do not retry blindly; calculate the local SHA-256 and run a read-only rehearsal.";

    public EvidenceUploadOutcomeUnknownException()
        : base(SafeMessage)
    {
    }

    public EvidenceUploadOutcomeUnknownException(Exception innerException)
        : base(SafeMessage, innerException)
    {
    }
}
