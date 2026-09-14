using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace GoldSrcOps.GameEventAgent;

internal sealed record GameEventAgentOptions(
    GameEventQueueOptions Queue,
    GameEventSpoolOptions Spool,
    GameEventDeliveryOptions? Delivery)
{
    private const string SectionName = "GameEventAgent";

    public static GameEventAgentOptions FromConfiguration(
        IConfiguration configuration,
        string contentRootPath)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

        var section = configuration.GetSection(SectionName);
        var queuePath = ResolvePath(
            ReadString(section, "QueuePath", "data/game-event-agent.db"),
            contentRootPath,
            "QueuePath");
        var queue = new GameEventQueueOptions(
            queuePath,
            ReadInt(section, "QueueCapacity", 10_000, 1, 100_000),
            TimeSpan.FromSeconds(
                ReadInt(section, "DatabaseBusyTimeoutSeconds", 5, 1, 60)));

        var spoolSection = section.GetSection("Spool");
        var spool = new GameEventSpoolOptions(
            ReadBool(spoolSection, "Enabled", defaultValue: false),
            ResolvePath(
                ReadString(spoolSection, "RootPath", "data/game-event-spool"),
                contentRootPath,
                "Spool:RootPath"),
            TimeSpan.FromSeconds(
                ReadInt(spoolSection, "ImportIntervalSeconds", 1, 1, 60)),
            ReadInt(spoolSection, "BatchSize", 20, 1, 1_000));

        var deliverySection = section.GetSection("Delivery");
        if (!ReadBool(deliverySection, "Enabled", defaultValue: false))
        {
            return new GameEventAgentOptions(queue, spool, Delivery: null);
        }

        var requestTimeout = TimeSpan.FromSeconds(
            ReadInt(deliverySection, "RequestTimeoutSeconds", 10, 1, 120));
        var lease = TimeSpan.FromSeconds(
            ReadInt(deliverySection, "LeaseSeconds", 30, 5, 600));
        if (lease <= requestTimeout + requestTimeout)
        {
            throw InvalidValue(
                "Delivery:LeaseSeconds",
                FormattableString.Invariant(
                    $"a value greater than twice RequestTimeoutSeconds ({requestTimeout.TotalSeconds:0})"));
        }

        var retryBase = TimeSpan.FromSeconds(
            ReadInt(deliverySection, "RetryBaseSeconds", 1, 1, 300));
        var retryMaximum = TimeSpan.FromSeconds(
            ReadInt(deliverySection, "RetryMaximumSeconds", 300, 1, 86_400));
        if (retryMaximum < retryBase)
        {
            throw InvalidValue(
                "Delivery:RetryMaximumSeconds",
                "a value greater than or equal to RetryBaseSeconds");
        }

        var oauthSection = deliverySection.GetSection("OAuth");
        var delivery = new GameEventDeliveryOptions(
            ReadGuid(deliverySection, "ServerId"),
            ReadHttpUri(deliverySection, "ApiBaseUrl", allowLoopbackHttp: true, ensureTrailingSlash: true),
            TimeSpan.FromSeconds(
                ReadInt(deliverySection, "DispatchIntervalSeconds", 1, 1, 60)),
            lease,
            ReadInt(deliverySection, "BatchSize", 20, 1, 1_000),
            ReadInt(deliverySection, "MaximumAttempts", 12, 1, 100),
            TimeSpan.FromDays(
                ReadInt(deliverySection, "MaximumEventAgeDays", 30, 1, 44)),
            retryBase,
            retryMaximum,
            requestTimeout,
            new OAuthClientCredentialsOptions(
                ReadHttpUri(oauthSection, "TokenEndpoint", allowLoopbackHttp: true, ensureTrailingSlash: false),
                ReadRequiredString(oauthSection, "ClientId", maximumLength: 256),
                ResolvePath(
                    ReadRequiredString(oauthSection, "ClientSecretFile", maximumLength: 1_024),
                    contentRootPath,
                    "Delivery:OAuth:ClientSecretFile"),
                ReadRequiredString(oauthSection, "Audience", maximumLength: 512),
                ReadString(oauthSection, "Scope", "ingest:game-events"),
                TimeSpan.FromSeconds(
                    ReadInt(oauthSection, "RefreshSkewSeconds", 60, 0, 600))));

        return new GameEventAgentOptions(queue, spool, delivery);
    }

    private static string ReadRequiredString(
        IConfiguration section,
        string key,
        int maximumLength)
    {
        var value = section[key]?.Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
        {
            throw InvalidValue(
                ConfigurationPath(section, key),
                $"a non-empty value no longer than {maximumLength.ToString(CultureInfo.InvariantCulture)} characters");
        }

        return value;
    }

    private static string ReadString(
        IConfiguration section,
        string key,
        string defaultValue)
    {
        var value = section[key];
        return value is null ? defaultValue : value.Trim();
    }

    private static bool ReadBool(IConfiguration section, string key, bool defaultValue)
    {
        var value = section[key];
        if (value is null)
        {
            return defaultValue;
        }

        if (bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        throw InvalidValue(ConfigurationPath(section, key), "a Boolean value");
    }

    private static int ReadInt(
        IConfiguration section,
        string key,
        int defaultValue,
        int minValue,
        int maxValue)
    {
        var value = section[key];
        if (value is null)
        {
            return defaultValue;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
            parsed >= minValue &&
            parsed <= maxValue)
        {
            return parsed;
        }

        throw InvalidValue(
            ConfigurationPath(section, key),
            $"an integer between {minValue.ToString(CultureInfo.InvariantCulture)} and {maxValue.ToString(CultureInfo.InvariantCulture)}");
    }

    private static Guid ReadGuid(IConfiguration section, string key)
    {
        var value = section[key];
        if (Guid.TryParse(value, out var parsed) && parsed != Guid.Empty)
        {
            return parsed;
        }

        throw InvalidValue(ConfigurationPath(section, key), "a non-empty UUID");
    }

    private static Uri ReadHttpUri(
        IConfiguration section,
        string key,
        bool allowLoopbackHttp,
        bool ensureTrailingSlash)
    {
        var value = section[key];
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                !(allowLoopbackHttp &&
                    string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                    uri.IsLoopback)))
        {
            throw InvalidValue(
                ConfigurationPath(section, key),
                "an HTTPS URI without credentials, query, or fragment (HTTP is allowed only for loopback)");
        }

        if (!ensureTrailingSlash || uri.AbsoluteUri[^1] == '/')
        {
            return uri;
        }

        return new Uri(string.Concat(uri.AbsoluteUri, "/"), UriKind.Absolute);
    }

    private static string ResolvePath(string value, string contentRootPath, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw InvalidValue(key, "a non-empty filesystem path");
        }

        try
        {
            return Path.GetFullPath(value, contentRootPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException(
                $"Configuration value '{SectionName}:{key}' is not a valid filesystem path.",
                exception);
        }
    }

    private static string ConfigurationPath(IConfiguration section, string key)
    {
        var sectionPath = section is IConfigurationSection configurationSection
            ? configurationSection.Path
            : SectionName;
        if (string.Equals(sectionPath, SectionName, StringComparison.Ordinal))
        {
            return key;
        }

        return string.Concat(sectionPath[(SectionName.Length + 1)..], ":", key);
    }

    private static InvalidOperationException InvalidValue(string key, string expected) =>
        new($"Configuration value '{SectionName}:{key}' must be {expected}.");
}

internal sealed record GameEventQueueOptions(
    string DatabasePath,
    int Capacity,
    TimeSpan BusyTimeout,
    bool Pooling = true);

internal sealed record GameEventSpoolOptions(
    bool Enabled,
    string RootPath,
    TimeSpan ImportInterval,
    int BatchSize)
{
    public string IncomingPath => Path.Combine(RootPath, "incoming");

    public string ProcessingPath => Path.Combine(RootPath, "processing");

    public string AcceptedPath => Path.Combine(RootPath, "accepted");

    public string RejectedPath => Path.Combine(RootPath, "rejected");
}

internal sealed record GameEventDeliveryOptions(
    Guid ServerId,
    Uri ApiBaseUri,
    TimeSpan DispatchInterval,
    TimeSpan LeaseDuration,
    int BatchSize,
    int MaximumAttempts,
    TimeSpan MaximumEventAge,
    TimeSpan RetryBaseDelay,
    TimeSpan RetryMaximumDelay,
    TimeSpan RequestTimeout,
    OAuthClientCredentialsOptions OAuth);

internal sealed record OAuthClientCredentialsOptions(
    Uri TokenEndpoint,
    string ClientId,
    string ClientSecretFile,
    string Audience,
    string Scope,
    TimeSpan RefreshSkew);
