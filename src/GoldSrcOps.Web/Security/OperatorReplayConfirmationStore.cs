using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;

namespace GoldSrcOps.Web.Security;

internal sealed class OperatorReplayConfirmationStore(TimeProvider timeProvider)
{
    internal const int TokenLength = 43;
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    internal const int Capacity = 1_024;
    private const int EntropyBytes = 32;

    private readonly Lock sync = new();
    private readonly Dictionary<string, Confirmation> confirmations = new(StringComparer.Ordinal);

    public string? Issue(string subject, Guid eventId, Guid requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentOutOfRangeException.ThrowIfEqual(eventId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(requestId, Guid.Empty);

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
                new Confirmation(subject, eventId, requestId, now + Lifetime));
            return token;
        }
    }

    public bool TryConsume(string token, string subject, Guid eventId, Guid requestId)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentOutOfRangeException.ThrowIfEqual(eventId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(requestId, Guid.Empty);

        if (token.Length != TokenLength)
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();

        lock (sync)
        {
            if (!confirmations.TryGetValue(token, out var confirmation) ||
                confirmation.ExpiresAtUtc <= now ||
                confirmation.EventId != eventId ||
                confirmation.RequestId != requestId ||
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
        Guid EventId,
        Guid RequestId,
        DateTimeOffset ExpiresAtUtc);
}
