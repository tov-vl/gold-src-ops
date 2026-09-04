using System.Globalization;
using System.Net.Http.Headers;
using System.Text;

namespace GoldSrcOps.AvailabilityExporter;

internal sealed class GrafanaLogsApiClient : IProbeFailureDetailSource
{
    private readonly HttpClient _httpClient;
    private readonly GrafanaLogsApiOptions _options;
    private readonly Uri _queryRangeEndpoint;
    private readonly AuthenticationHeaderValue _authorization;

    public GrafanaLogsApiClient(HttpClient httpClient, GrafanaLogsApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        _httpClient = httpClient;
        _options = options;
        _queryRangeEndpoint = new Uri(EnsureTrailingSlash(options.QueryEndpoint), "loki/api/v1/query_range");
        _authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.LogsUser}:{options.LogsToken}")));
    }

    public TimeSpan CorrelationTolerance => _options.CorrelationTolerance;

    public async Task<IReadOnlyList<ProbeFailureDetail>> QueryAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken)
    {
        if (endUtc <= startUtc)
        {
            throw new ArgumentException("The query range must not be empty.", nameof(endUtc));
        }

        var query = LokiQueryBuilder.BuildFailedHttpProbeQuery(_options);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            BuildQueryUri(query, startUtc, endUtc));
        request.Headers.Authorization = _authorization;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("GoldSrcOps-AvailabilityExporter/1.0");

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_options.RequestTimeout);

        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutSource.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new GrafanaLogsApiException(
                    FormattableString.Invariant(
                        $"The logs API returned HTTP status {(int)response.StatusCode}."));
            }

            if (response.Content.Headers.ContentLength is { } contentLength &&
                contentLength > _options.MaximumResponseBytes)
            {
                throw new GrafanaLogsApiException("The logs API response exceeded the size limit.");
            }

            await response.Content.LoadIntoBufferAsync(
                _options.MaximumResponseBytes,
                timeoutSource.Token).ConfigureAwait(false);
            var payload = await response.Content.ReadAsByteArrayAsync(timeoutSource.Token).ConfigureAwait(false);
            return LokiFailureDetailsParser.Parse(payload, _options.MaximumLines);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GrafanaLogsApiException("The logs API request timed out.");
        }
        catch (LokiResponseLimitExceededException exception)
        {
            throw new GrafanaLogsApiException(
                "The logs API response exceeded the line limit.",
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new GrafanaLogsApiException("The logs API request failed.", exception);
        }
        catch (InvalidDataException exception)
        {
            throw new GrafanaLogsApiException("The logs API returned an invalid response.", exception);
        }
    }

    private Uri BuildQueryUri(
        string query,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc)
    {
        var builder = new UriBuilder(_queryRangeEndpoint)
        {
            Query = string.Join(
                "&",
                $"query={Uri.EscapeDataString(query)}",
                $"start={Uri.EscapeDataString(startUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))}",
                $"end={Uri.EscapeDataString(endUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))}",
                $"limit={checked(_options.MaximumLines + 1).ToString(CultureInfo.InvariantCulture)}",
                "direction=forward"),
        };

        return builder.Uri;
    }

    private static Uri EnsureTrailingSlash(Uri endpoint)
    {
        var value = endpoint.AbsoluteUri;
        return value.EndsWith('/')
            ? endpoint
            : new Uri($"{value}/", UriKind.Absolute);
    }

    private static void ValidateOptions(GrafanaLogsApiOptions options)
    {
        if (!options.QueryEndpoint.IsAbsoluteUri ||
            !string.Equals(options.QueryEndpoint.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(options.QueryEndpoint.UserInfo) ||
            !string.IsNullOrEmpty(options.QueryEndpoint.Query) ||
            !string.IsNullOrEmpty(options.QueryEndpoint.Fragment))
        {
            throw new ArgumentException(
                "The logs query endpoint must be an HTTPS base URL without credentials, query, or fragment.",
                nameof(options));
        }

        ValidateText(options.LogsUser, nameof(options.LogsUser));
        ValidateText(options.LogsToken, nameof(options.LogsToken));
        ValidateText(options.Job, nameof(options.Job));
        ValidateText(options.Probe, nameof(options.Probe));
        ValidateText(options.Environment, nameof(options.Environment));
        ValidateText(options.Role, nameof(options.Role));
        ValidateText(options.MonitorRevision, nameof(options.MonitorRevision));

        if (options.LogsUser.Contains(':'))
        {
            throw new ArgumentException("The logs API user must not contain a colon.", nameof(options));
        }

        if (options.RequestTimeout <= TimeSpan.Zero || options.RequestTimeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (options.MaximumResponseBytes is < 1024 or > 64 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (options.MaximumLines is < 1 or > 20_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (options.CorrelationTolerance <= TimeSpan.Zero ||
            options.CorrelationTolerance > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    private static void ValidateText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Any(char.IsControl))
        {
            throw new ArgumentException("Control characters are not allowed.", parameterName);
        }
    }
}
