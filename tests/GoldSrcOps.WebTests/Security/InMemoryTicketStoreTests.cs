using System.Security.Claims;
using AwesomeAssertions;
using GoldSrcOps.Web.Security;
using Microsoft.AspNetCore.Authentication;

namespace GoldSrcOps.WebTests.Security;

public sealed class InMemoryTicketStoreTests
{
    private static readonly DateTimeOffset ReferenceTime =
        new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Stored_ticket_can_be_retrieved_renewed_and_removed()
    {
        var timeProvider = new MutableTimeProvider(ReferenceTime);
        var store = new InMemoryTicketStore(timeProvider);
        var key = await store.StoreAsync(CreateTicket("reader", ReferenceTime.AddMinutes(30)));

        var stored = await store.RetrieveAsync(key);
        await store.RenewAsync(key, CreateTicket("operator", ReferenceTime.AddMinutes(30)));
        var renewed = await store.RetrieveAsync(key);
        await store.RemoveAsync(key);

        stored!.Principal.Identity!.Name.Should().Be("reader");
        renewed!.Principal.Identity!.Name.Should().Be("operator");
        (await store.RetrieveAsync(key)).Should().BeNull();
    }

    [Fact]
    public async Task Expired_ticket_is_rejected_and_removed()
    {
        var timeProvider = new MutableTimeProvider(ReferenceTime);
        var store = new InMemoryTicketStore(timeProvider);
        var key = await store.StoreAsync(CreateTicket("reader", ReferenceTime.AddMinutes(1)));

        timeProvider.Advance(TimeSpan.FromMinutes(2));

        (await store.RetrieveAsync(key)).Should().BeNull();
        (await store.RetrieveAsync(key)).Should().BeNull();
    }

    [Fact]
    public async Task Concurrent_stores_cannot_exceed_session_capacity()
    {
        var timeProvider = new MutableTimeProvider(ReferenceTime);
        var store = new InMemoryTicketStore(timeProvider);
        var tasks = Enumerable.Range(0, 1100)
            .Select(index => Task.Run(async () =>
            {
                try
                {
                    await store.StoreAsync(CreateTicket(
                        $"reader-{index}",
                        ReferenceTime.AddMinutes(30)));
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        results.Should().HaveCount(1100);
        results.Count(success => success).Should().Be(1024);
        results.Count(success => !success).Should().Be(76);
    }

    private static AuthenticationTicket CreateTicket(string name, DateTimeOffset expiresUtc)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, name)],
            "Test",
            ClaimTypes.Name,
            ClaimTypes.Role);
        return new AuthenticationTicket(
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { ExpiresUtc = expiresUtc },
            "Test");
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
