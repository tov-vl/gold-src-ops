using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using GoldSrcOps.Contracts.Alerts;
using GoldSrcOps.Contracts.Monitoring;
using GoldSrcOps.Contracts.ProviderDelivery;
using GoldSrcOps.Web.Security;
using GoldSrcOps.Web.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GoldSrcOps.WebTests.Pages;

public sealed class CompleteHealthReportTests
{
    private const string Path = "/operator/game-host-health/report";
    public static TheoryData<int> FailureMasks => new(Enumerable.Range(0, 64));

    [Theory]
    [MemberData(nameof(FailureMasks))]
    public async Task Every_source_failure_is_independent_bounded_and_not_retried(int failures)
    {
        var fixture = new Sources { Failures = failures };
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        await using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => Register(services, fixture)));
        using var client = configured.CreateClient();
        using var response = await client.GetAsync(Path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle().Which.Should().Be("nosniff");
        response.Content.Headers.ContentDisposition!.FileNameStar.Should().Be("goldsrcops-game-host-health.txt");
        response.Content.Headers.ContentType!.CharSet.Should().Be("utf-8");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Length.Should().BeLessThan(32768);
        var report = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        var labels = new[] { "Snapshot", "Archive observation", "Polling observation", "Credential snapshot", "Incident outbox snapshot", "Incident receiver snapshot" };
        for (var index = 0; index < labels.Length; index++)
        {
            report.Should().Contain(labels[index] + ": " + ((failures & (1 << index)) != 0 ? "Unknown" : index == 2 ? "Running" : "Available"));
        }

        fixture.Reads.Should().Equal(1, 1, 1, 1, 1, 1);
        report.Should().NotContain("private-sentinel").And.NotContain("Authorization").And.NotContain("Bearer");
    }

    [Theory]
    [InlineData(WebSecurity.ReaderRole)]
    [InlineData(null)]
    public async Task Denied_role_does_not_read_any_source(string? role)
    {
        var fixture = new Sources();
        await using var factory = new ReaderWebApplicationFactory(role);
        await using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => Register(services, fixture)));
        using var client = configured.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync(Path);
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        fixture.Reads.Should().OnlyContain(static value => value == 0);
    }

    [Fact]
    public async Task Disabled_receiver_is_not_queried_or_invented_as_an_empty_queue()
    {
        var fixture = new Sources { IsReceiverConfigured = false };
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        await using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => Register(services, fixture)));
        using var client = configured.CreateClient();
        var report = await client.GetStringAsync(Path);
        report.Should().Contain("Incident receiver snapshot: Disabled").And.Contain("Pending: Not queried");
        fixture.Reads.Should().Equal(1, 1, 1, 1, 1, 0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(63)]
    public async Task Concurrent_reads_propagate_caller_cancellation_without_a_report(int waiting)
    {
        var fixture = new Sources { Waiting = waiting == 63 ? 63 : 1 << waiting };
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        await using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => Register(services, fixture)));
        using var client = configured.CreateClient();
        using var cancellation = new CancellationTokenSource();
        var request = client.GetAsync(Path, cancellation.Token);
        try
        {
            await Task.WhenAll(fixture.Started.Select(static item => item.Task)).WaitAsync(TimeSpan.FromSeconds(10), cancellation.Token);
        }
        finally
        {
            await cancellation.CancelAsync();
        }

        var read = async () => await request.ConfigureAwait(false);
        await read.Should().ThrowAsync<OperationCanceledException>();
        // A cancelled request cannot supply the deadline for observing its own completion.
        await Task.WhenAll(fixture.Cancelled.Where((_, index) => (fixture.Waiting & (1 << index)) != 0).Select(static item => item.Task))
            .WaitAsync(TimeSpan.FromSeconds(10));
        fixture.Reads.Should().Equal(1, 1, 1, 1, 1, 1);
    }

    internal static void ConfigureSupplement(IServiceCollection services)
    {
        var fixture = new Sources();
        services.RemoveAll<ICredentialExpiryClient>();
        services.AddSingleton<ICredentialExpiryClient>(fixture);
        services.RemoveAll<IIncidentDeliveryHealthClient>();
        services.AddSingleton<IIncidentDeliveryHealthClient>(fixture);
    }

    private static void Register(IServiceCollection services, Sources fixture)
    {
        services.RemoveAll<IGameHostHealthClient>(); services.AddSingleton<IGameHostHealthClient>(fixture);
        services.RemoveAll<IArchiveHealthClient>(); services.AddSingleton<IArchiveHealthClient>(fixture);
        services.RemoveAll<IPollingHealthClient>(); services.AddSingleton<IPollingHealthClient>(fixture);
        services.RemoveAll<ICredentialExpiryClient>(); services.AddSingleton<ICredentialExpiryClient>(fixture);
        services.RemoveAll<IIncidentDeliveryHealthClient>(); services.AddSingleton<IIncidentDeliveryHealthClient>(fixture);
    }

    private sealed class Sources : IGameHostHealthClient, IArchiveHealthClient, IPollingHealthClient, ICredentialExpiryClient, IIncidentDeliveryHealthClient
    {
        public int Failures { get; init; }
        public int Waiting { get; init; }
        public bool IsReceiverConfigured { get; init; } = true;
        public int[] Reads { get; } = new int[6];
        public TaskCompletionSource[] Started { get; } = [.. Enumerable.Range(0, 6).Select(static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))];
        public TaskCompletionSource[] Cancelled { get; } = [.. Enumerable.Range(0, 6).Select(static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))];

        public async Task<GameHostHealthResponse> ReadAsync(CancellationToken cancellationToken = default) { await ReadAsync(0, cancellationToken).ConfigureAwait(false); return OperatorHealthOverviewFixtures.GameHost(DateTimeOffset.UtcNow); }
        async Task<ArchiveHealthResponse> IArchiveHealthClient.ReadAsync(CancellationToken cancellationToken) { await ReadAsync(1, cancellationToken).ConfigureAwait(false); return OperatorHealthOverviewFixtures.Archive(DateTimeOffset.UtcNow); }
        async Task<PollingHealthResponse> IPollingHealthClient.ReadAsync(CancellationToken cancellationToken) { await ReadAsync(2, cancellationToken).ConfigureAwait(false); return OperatorHealthOverviewFixtures.Polling(DateTimeOffset.UtcNow); }
        async Task<CredentialExpiryResponse> ICredentialExpiryClient.ReadAsync(CancellationToken cancellationToken) { await ReadAsync(3, cancellationToken).ConfigureAwait(false); return OperatorHealthOverviewFixtures.Credentials(DateTimeOffset.UtcNow); }
        public async Task<AlertDeliveryStatusResponse> ReadOutboxAsync(CancellationToken cancellationToken) { await ReadAsync(4, cancellationToken).ConfigureAwait(false); return OperatorHealthOverviewFixtures.Outbox(DateTimeOffset.UtcNow); }
        public async Task<ProviderDeliveryStatusResponse> ReadReceiverAsync(CancellationToken cancellationToken) { await ReadAsync(5, cancellationToken).ConfigureAwait(false); return OperatorHealthOverviewFixtures.Receiver(DateTimeOffset.UtcNow); }

        private async Task ReadAsync(int index, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Reads[index]);
            Started[index].TrySetResult();
            if ((Waiting & (1 << index)) != 0)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false); }
                finally { if (cancellationToken.IsCancellationRequested) { Cancelled[index].TrySetResult(); } }
            }

            if ((Failures & (1 << index)) != 0)
            {
                throw index switch
                {
                    0 => new HttpRequestException("private-sentinel"),
                    1 => new JsonException("private-sentinel"),
                    2 => new InvalidDataException("private-sentinel"),
                    3 => new TaskCanceledException("private-sentinel"),
                    _ => new OperationCanceledException("private-sentinel")
                };
            }
        }
    }
}
