using System.Text;
using GoldSrcOps.Application.Availability;

namespace GoldSrcOps.AvailabilityExporter;

internal static class ProbeFailureLogClassifier
{
    private static readonly string[] DnsErrorMarkers =
    [
        "no such host",
        "temporary failure in name resolution",
        "server misbehaving",
        "name resolution",
        "nodename nor servname",
    ];

    private static readonly string[] ConnectErrorMarkers =
    [
        "connect:",
        "connection refused",
        "network is unreachable",
        "no route to host",
        "connection reset by peer",
        "connection aborted",
    ];

    private static readonly string[] TlsErrorMarkers =
    [
        "tls:",
        "x509:",
        "certificate",
        "remote error: tls",
    ];

    private static readonly string[] TimeoutErrorMarkers =
    [
        "context deadline exceeded",
        "i/o timeout",
        "timeout",
        "timed out",
        "deadline exceeded",
    ];

    public static ProbeFailureKind? Classify(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (!TryReadLogfmtField(line, "msg", out var message))
        {
            return null;
        }

        if (string.Equals(
                message,
                "Resolution with IP protocol failed",
                StringComparison.OrdinalIgnoreCase))
        {
            return ProbeFailureKind.Dns;
        }

        if (string.Equals(
                message,
                "TLS certificate verification failed",
                StringComparison.OrdinalIgnoreCase))
        {
            return ProbeFailureKind.Tls;
        }

        if (!string.Equals(message, "HTTP request failed", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return TryReadLogfmtField(line, "err", out var error)
            ? ClassifyRequestError(error)
            : ProbeFailureKind.Monitor;
    }

    private static ProbeFailureKind ClassifyRequestError(string error)
    {
        var matches = new HashSet<ProbeFailureKind>();

        AddMatch(matches, error, DnsErrorMarkers, ProbeFailureKind.Dns);
        AddMatch(matches, error, ConnectErrorMarkers, ProbeFailureKind.Connect);
        AddMatch(matches, error, TlsErrorMarkers, ProbeFailureKind.Tls);
        AddMatch(matches, error, TimeoutErrorMarkers, ProbeFailureKind.Timeout);

        return matches.Count == 1
            ? matches.Single()
            : ProbeFailureKind.Monitor;
    }

    private static void AddMatch(
        HashSet<ProbeFailureKind> matches,
        string value,
        IEnumerable<string> markers,
        ProbeFailureKind failureKind)
    {
        if (markers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            matches.Add(failureKind);
        }
    }

    private static bool TryReadLogfmtField(string line, string fieldName, out string value)
    {
        var input = line.AsSpan();
        var index = 0;

        while (index < input.Length)
        {
            while (index < input.Length && char.IsWhiteSpace(input[index]))
            {
                index++;
            }

            var keyStart = index;
            while (index < input.Length &&
                   !char.IsWhiteSpace(input[index]) &&
                   input[index] != '=')
            {
                index++;
            }

            if (index >= input.Length || input[index] != '=')
            {
                while (index < input.Length && !char.IsWhiteSpace(input[index]))
                {
                    index++;
                }

                continue;
            }

            var keyMatches = input[keyStart..index].Equals(fieldName, StringComparison.Ordinal);
            index++;

            if (index < input.Length && input[index] == '"')
            {
                index++;
                StringBuilder? builder = keyMatches ? new StringBuilder() : null;
                var closed = false;

                while (index < input.Length)
                {
                    var character = input[index++];
                    if (character == '"')
                    {
                        closed = true;
                        break;
                    }

                    if (character == '\\' && index < input.Length)
                    {
                        character = Unescape(input[index++]);
                    }

                    builder?.Append(character);
                }

                if (!closed)
                {
                    break;
                }

                if (keyMatches)
                {
                    value = builder!.ToString();
                    return true;
                }

                continue;
            }

            var valueStart = index;
            while (index < input.Length && !char.IsWhiteSpace(input[index]))
            {
                index++;
            }

            if (keyMatches)
            {
                value = input[valueStart..index].ToString();
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static char Unescape(char value) => value switch
    {
        'n' => '\n',
        'r' => '\r',
        't' => '\t',
        _ => value,
    };
}
