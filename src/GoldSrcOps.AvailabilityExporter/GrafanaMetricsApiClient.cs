using System.Globalization;
using System.Net.Http.Headers;
using System.Text;

namespace GoldSrcOps.AvailabilityExporter;

internal sealed class GrafanaMetricsApiClient
{
    private readonly HttpClient _httpClient;
    private readonly GrafanaMetricsApiOptions _options;
    private readonly Uri _queryRangeEndpoint;
    private readonly AuthenticationHeaderValue _authorization;

    public GrafanaMetricsApiClient(HttpClient httpClient, GrafanaMetricsApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        _httpClient = httpClient;
        _options = options;
        _queryRangeEndpoint = new Uri(EnsureTrailingSlash(options.QueryEndpoint), "api/v1/query_range");
        _authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.MetricsUser}:{options.MetricsToken}")));
    }

    public async Task<IReadOnlyList<PrometheusSeries>> QueryRangeAsync(
        string query,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (endUtc <= startUtc)
        {
            throw new ArgumentException("The query range must not be empty.", nameof(endUtc));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, _queryRangeEndpoint)
        {
            Content = new FormUrlEncodedContent(
            [
                new("query", query),
                new("start", startUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
                new("end", endUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
                new("step", FormattableString.Invariant($"{_options.QueryStep.TotalSeconds:0.###}")),
            ]),
        };
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
                throw new GrafanaMetricsApiException(
                    FormattableString.Invariant(
                        $"The metrics API returned HTTP status {(int)response.StatusCode}."));
            }

            if (response.Content.Headers.ContentLength is { } contentLength &&
                contentLength > _options.MaximumResponseBytes)
            {
                throw new GrafanaMetricsApiException("The metrics API response exceeded the size limit.");
            }

            await response.Content.LoadIntoBufferAsync(
                _options.MaximumResponseBytes,
                timeoutSource.Token).ConfigureAwait(false);
            var payload = await response.Content.ReadAsByteArrayAsync(timeoutSource.Token).ConfigureAwait(false);
            return PrometheusMatrixParser.Parse(payload);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GrafanaMetricsApiException("The metrics API request timed out.");
        }
        catch (HttpRequestException ex)
        {
            throw new GrafanaMetricsApiException("The metrics API request failed.", ex);
        }
        catch (InvalidDataException ex)
        {
            throw new GrafanaMetricsApiException("The metrics API returned an invalid response.", ex);
        }
    }

    private static Uri EnsureTrailingSlash(Uri endpoint)
    {
        var value = endpoint.AbsoluteUri;
        return value.EndsWith('/')
            ? endpoint
            : new Uri($"{value}/", UriKind.Absolute);
    }

    private static void ValidateOptions(GrafanaMetricsApiOptions options)
    {
        if (!options.QueryEndpoint.IsAbsoluteUri ||
            !string.Equals(options.QueryEndpoint.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(options.QueryEndpoint.UserInfo) ||
            !string.IsNullOrEmpty(options.QueryEndpoint.Query) ||
            !string.IsNullOrEmpty(options.QueryEndpoint.Fragment))
        {
            throw new ArgumentException(
                "The metrics query endpoint must be an HTTPS base URL without credentials, query, or fragment.",
                nameof(options));
        }

        ValidateText(options.MetricsUser, nameof(options.MetricsUser));
        ValidateText(options.MetricsToken, nameof(options.MetricsToken));
        ValidateText(options.Job, nameof(options.Job));
        ValidateText(options.Probe, nameof(options.Probe));
        ValidateText(options.Environment, nameof(options.Environment));
        ValidateText(options.Role, nameof(options.Role));
        ValidateText(options.MonitorRevision, nameof(options.MonitorRevision));
        ValidateText(options.Location, nameof(options.Location));

        if (options.MetricsUser.Contains(':'))
        {
            throw new ArgumentException("The metrics API user must not contain a colon.", nameof(options));
        }

        if (options.QueryStep < TimeSpan.FromSeconds(1) || options.QueryStep > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (options.RequestTimeout <= TimeSpan.Zero || options.RequestTimeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (options.MaximumResponseBytes is < 1024 or > 64 * 1024 * 1024)
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
