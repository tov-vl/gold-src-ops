using System.Globalization;

namespace GoldSrcOps.Api.Hosting;

internal sealed class PublicServerJoinOptions
{
    private const string SectionName = "PublicServerJoin";

    public bool Enabled { get; init; }

    public Guid ServerId { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Host { get; init; } = string.Empty;

    public int Port { get; init; }

    public static PublicServerJoinOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);
        var serverIdValue = section["ServerId"];
        var nameValue = section["Name"];
        var hostValue = section["Host"];
        var portValue = section["Port"];
        if (string.IsNullOrWhiteSpace(serverIdValue) &&
            string.IsNullOrWhiteSpace(nameValue) &&
            string.IsNullOrWhiteSpace(hostValue) &&
            string.IsNullOrWhiteSpace(portValue))
        {
            return new PublicServerJoinOptions();
        }

        if (!Guid.TryParse(serverIdValue, out var serverId) || serverId == Guid.Empty)
        {
            throw InvalidValue("ServerId", "a non-empty GUID when public server join is configured");
        }

        var name = ReadBoundedText(nameValue, "Name", maxLength: 80);
        var host = ReadHost(hostValue);
        if (!int.TryParse(portValue, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is < 1 or > 65535)
        {
            throw InvalidValue("Port", "an integer between 1 and 65535 when public server join is configured");
        }

        return new PublicServerJoinOptions
        {
            Enabled = true,
            ServerId = serverId,
            Name = name,
            Host = host,
            Port = port
        };
    }

    private static string ReadBoundedText(string? value, string key, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Length > maxLength ||
            value.Any(char.IsControl))
        {
            throw InvalidValue(
                key,
                $"non-empty text of at most {maxLength.ToString(CultureInfo.InvariantCulture)} characters without surrounding whitespace or control characters");
        }

        return value;
    }

    private static string ReadHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Length > 253 ||
            Uri.CheckHostName(value) is not (UriHostNameType.Dns or UriHostNameType.IPv4))
        {
            throw InvalidValue("Host", "a DNS name or IPv4 address without a scheme, path, or port");
        }

        return value.ToLowerInvariant();
    }

    private static InvalidOperationException InvalidValue(string key, string expected) =>
        new($"Configuration value '{SectionName}:{key}' must be {expected}.");
}
