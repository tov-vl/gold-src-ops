using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;

namespace GoldSrcOps.Web.Security;

internal sealed class OperatorCommandConfirmationStore(TimeProvider timeProvider)
{
    internal const int TokenLength = 43;
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    internal const int Capacity = 1_024;
    private const int EntropyBytes = 32;

    private readonly Lock sync = new();
    private readonly Dictionary<string, Confirmation> confirmations = new(StringComparer.Ordinal);

    public string? Issue(
        string subject,
        Guid serverId,
        OperatorCommandAction action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentOutOfRangeException.ThrowIfEqual(serverId, Guid.Empty);
        ValidateAction(action);

        var now = timeProvider.GetUtcNow();

        lock (sync)
        {
            RemoveExpired(now);
            if (confirmations.Count >= Capacity)
            {
                return null;
            }

            string token;
            do
            {
                token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(EntropyBytes));
            }
            while (confirmations.ContainsKey(token));

            confirmations.Add(
                token,
                new Confirmation(subject, serverId, action, now + Lifetime));
            return token;
        }
    }

    public bool TryConsume(
        string token,
        string subject,
        Guid serverId,
        OperatorCommandAction action)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentOutOfRangeException.ThrowIfEqual(serverId, Guid.Empty);
        ValidateAction(action);

        if (token.Length != TokenLength)
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();

        lock (sync)
        {
            if (!confirmations.TryGetValue(token, out var confirmation) ||
                confirmation.ExpiresAtUtc <= now ||
                confirmation.ServerId != serverId ||
                confirmation.Action != action ||
                !string.Equals(confirmation.Subject, subject, StringComparison.Ordinal))
            {
                if (confirmation is not null && confirmation.ExpiresAtUtc <= now)
                {
                    confirmations.Remove(token);
                }

                return false;
            }

            confirmations.Remove(token);
            return true;
        }
    }

    private static void ValidateAction(OperatorCommandAction action)
    {
        if (!Enum.IsDefined(action))
        {
            throw new ArgumentOutOfRangeException(nameof(action));
        }
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var token in confirmations
                     .Where(pair => pair.Value.ExpiresAtUtc <= now)
                     .Select(static pair => pair.Key)
                     .ToArray())
        {
            confirmations.Remove(token);
        }
    }

    private sealed record Confirmation(
        string Subject,
        Guid ServerId,
        OperatorCommandAction Action,
        DateTimeOffset ExpiresAtUtc);
}

internal enum OperatorCommandAction
{
    Say,
    Restart
}
