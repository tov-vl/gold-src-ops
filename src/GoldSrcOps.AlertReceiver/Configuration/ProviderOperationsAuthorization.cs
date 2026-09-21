using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace GoldSrcOps.AlertReceiver.Configuration;

internal sealed class ProviderOperationsAuthorization
{
    private readonly byte[] _expectedHash;

    public ProviderOperationsAuthorization(IOptions<ProviderOperationsOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        IsEnabled = options.Value.Enabled;
        _expectedHash = Hash(options.Value.Authorization);
    }

    public bool IsEnabled { get; }

    public bool IsAuthorized(StringValues authorizationValues)
    {
        if (!IsEnabled ||
            authorizationValues.Count != 1 ||
            string.IsNullOrEmpty(authorizationValues[0]) ||
            authorizationValues[0]!.Length > ProviderOperationsOptions.MaxAuthorizationLength)
        {
            return false;
        }

        var providedHash = Hash(authorizationValues[0]!);
        return CryptographicOperations.FixedTimeEquals(_expectedHash, providedHash);
    }

    private static byte[] Hash(string value) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
