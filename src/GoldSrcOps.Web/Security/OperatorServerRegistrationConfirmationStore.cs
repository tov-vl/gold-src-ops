using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;

namespace GoldSrcOps.Web.Security;

internal sealed class OperatorServerRegistrationConfirmationStore(TimeProvider timeProvider)
{
    internal const int TokenLength = 43;
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    internal const int Capacity = 1_024;
    private const int EntropyBytes = 32;

    private readonly Lock sync = new();
    private readonly Dictionary<string, Confirmation> confirmations = new(StringComparer.Ordinal);

    public string? Issue(string subject, OperatorServerRegistrationDraft draft)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.RequestId == Guid.Empty)
        {
            throw new ArgumentException("Registration request ID must not be empty.", nameof(draft));
        }

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
                new Confirmation(subject, draft, now + Lifetime));
            return token;
        }
    }

    public bool TryConsume(
        string token,
        string subject,
        out OperatorServerRegistrationDraft draft)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        draft = null!;
        if (token.Length != TokenLength)
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();

        lock (sync)
        {
            if (!confirmations.TryGetValue(token, out var confirmation) ||
                confirmation.ExpiresAtUtc <= now ||
                !string.Equals(confirmation.Subject, subject, StringComparison.Ordinal))
            {
                if (confirmation is not null && confirmation.ExpiresAtUtc <= now)
                {
                    confirmations.Remove(token);
                }

                return false;
            }

            confirmations.Remove(token);
            draft = confirmation.Draft;
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
        OperatorServerRegistrationDraft Draft,
        DateTimeOffset ExpiresAtUtc);
}

internal sealed record OperatorServerRegistrationDraft(
    Guid RequestId,
    string Name,
    string Host,
    int QueryPort,
    int? RconPort,
    int PollIntervalSeconds,
    string? Notes);
