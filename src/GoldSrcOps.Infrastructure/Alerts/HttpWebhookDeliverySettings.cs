using System.Net.Http.Headers;

namespace GoldSrcOps.Infrastructure.Alerts;

internal sealed record HttpWebhookDeliverySettings
{
    public HttpWebhookDeliverySettings(
        Uri endpoint,
        TimeSpan requestTimeout,
        TimeSpan maximumRetryAfter,
        string? authorization = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (!endpoint.IsAbsoluteUri ||
            (!endpoint.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !endpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Webhook endpoint must be an absolute HTTP or HTTPS URI.", nameof(endpoint));
        }

        if (!string.IsNullOrEmpty(endpoint.UserInfo))
        {
            throw new ArgumentException("Webhook endpoint must not contain user information.", nameof(endpoint));
        }

        if (requestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestTimeout),
                requestTimeout,
                "Webhook request timeout must be positive.");
        }

        if (maximumRetryAfter <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumRetryAfter),
                maximumRetryAfter,
                "Maximum Retry-After value must be positive.");
        }

        AuthenticationHeaderValue? parsedAuthorization = null;
        if (!string.IsNullOrWhiteSpace(authorization) &&
            !AuthenticationHeaderValue.TryParse(authorization, out parsedAuthorization))
        {
            throw new ArgumentException("Webhook authorization value is invalid.", nameof(authorization));
        }

        Endpoint = endpoint;
        RequestTimeout = requestTimeout;
        MaximumRetryAfter = maximumRetryAfter;
        Authorization = parsedAuthorization;
    }

    public Uri Endpoint { get; }

    public TimeSpan RequestTimeout { get; }

    public TimeSpan MaximumRetryAfter { get; }

    public AuthenticationHeaderValue? Authorization { get; }
}
