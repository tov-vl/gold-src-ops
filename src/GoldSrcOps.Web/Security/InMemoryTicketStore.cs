using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.WebUtilities;

namespace GoldSrcOps.Web.Security;

internal sealed class InMemoryTicketStore(TimeProvider timeProvider) : ITicketStore
{
    private const int SessionKeySize = 32;
    private const int MaximumSessions = 1024;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, StoredTicket> _tickets = new(StringComparer.Ordinal);

    public Task<string> StoreAsync(AuthenticationTicket ticket) =>
        StoreCoreAsync(ticket, CancellationToken.None);

    public Task<string> StoreAsync(
        AuthenticationTicket ticket,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        StoreCoreAsync(ticket, cancellationToken);

    public Task RenewAsync(string key, AuthenticationTicket ticket) =>
        RenewCoreAsync(key, ticket, CancellationToken.None);

    public Task RenewAsync(
        string key,
        AuthenticationTicket ticket,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        RenewCoreAsync(key, ticket, cancellationToken);

    public Task<AuthenticationTicket?> RetrieveAsync(string key) =>
        RetrieveCoreAsync(key, CancellationToken.None);

    public Task<AuthenticationTicket?> RetrieveAsync(
        string key,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        RetrieveCoreAsync(key, cancellationToken);

    public Task RemoveAsync(string key) => RemoveCoreAsync(key, CancellationToken.None);

    public Task RemoveAsync(
        string key,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        RemoveCoreAsync(key, cancellationToken);

    private Task<string> StoreCoreAsync(
        AuthenticationTicket ticket,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            RemoveExpiredTickets();

            if (_tickets.Count >= MaximumSessions)
            {
                throw new InvalidOperationException("The Web authentication session capacity was reached.");
            }

            string key;
            var storedTicket = Serialize(ticket);
            do
            {
                key = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(SessionKeySize));
            }
            while (!_tickets.TryAdd(key, storedTicket));

            return Task.FromResult(key);
        }
    }

    private Task RenewCoreAsync(
        string key,
        AuthenticationTicket ticket,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(ticket);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            RemoveExpiredTickets();
            if (!_tickets.TryGetValue(key, out var previous) && _tickets.Count >= MaximumSessions)
            {
                throw new InvalidOperationException("The Web authentication session capacity was reached.");
            }

            _tickets[key] = Serialize(ticket);
            if (previous is not null)
            {
                CryptographicOperations.ZeroMemory(previous.Payload);
            }
        }

        return Task.CompletedTask;
    }

    private Task<AuthenticationTicket?> RetrieveCoreAsync(
        string key,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_tickets.TryGetValue(key, out var storedTicket))
            {
                return Task.FromResult<AuthenticationTicket?>(null);
            }

            if (IsExpired(storedTicket.ExpiresUtc))
            {
                _tickets.Remove(key);
                CryptographicOperations.ZeroMemory(storedTicket.Payload);
                return Task.FromResult<AuthenticationTicket?>(null);
            }

            return Task.FromResult(TicketSerializer.Default.Deserialize(storedTicket.Payload));
        }
    }

    private Task RemoveCoreAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_tickets.Remove(key, out var storedTicket))
            {
                CryptographicOperations.ZeroMemory(storedTicket.Payload);
            }
        }

        return Task.CompletedTask;
    }

    private static StoredTicket Serialize(AuthenticationTicket ticket) =>
        new(TicketSerializer.Default.Serialize(ticket), ticket.Properties.ExpiresUtc);

    private void RemoveExpiredTickets()
    {
        foreach (var ticket in _tickets.Where(ticket => IsExpired(ticket.Value.ExpiresUtc)).ToArray())
        {
            _tickets.Remove(ticket.Key);
            CryptographicOperations.ZeroMemory(ticket.Value.Payload);
        }
    }

    private bool IsExpired(DateTimeOffset? expiresUtc) =>
        expiresUtc is not null && expiresUtc <= timeProvider.GetUtcNow();

    private sealed record StoredTicket(byte[] Payload, DateTimeOffset? ExpiresUtc);
}
