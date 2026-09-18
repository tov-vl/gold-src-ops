using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace GoldSrcOps.AlertReceiver.Configuration;

internal sealed class ReceiverAuthorization
{
    private readonly byte[] _expectedHash;

    public ReceiverAuthorization(IOptions<AlertReceiverOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _expectedHash = Hash(options.Value.Authorization);
    }

    public bool IsAuthorized(StringValues authorizationValues)
    {
        if (authorizationValues.Count != 1 ||
            string.IsNullOrEmpty(authorizationValues[0]) ||
            authorizationValues[0]!.Length > AlertReceiverOptions.MaxAuthorizationLength)
        {
            return false;
        }

        var providedHash = Hash(authorizationValues[0]!);
        return CryptographicOperations.FixedTimeEquals(_expectedHash, providedHash);
    }

    private static byte[] Hash(string value) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
