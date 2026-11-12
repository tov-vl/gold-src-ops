using System.Net;
using AwesomeAssertions;
using GoldSrcOps.Contracts.Monitoring;
using GoldSrcOps.Web.Security;
using GoldSrcOps.Web.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GoldSrcOps.WebTests.Pages;

public sealed class GameHostHealthReportTests
{
    private const string Path = "/operator/game-host-health/report";

    [Theory]
    [InlineData(WebSecurity.ReaderRole)]
    [InlineData(null)]
    public async Task Unauthorized_users_cannot_download_or_trigger_health_read(string? role)
    {
        var fixture = new FixtureClient();
        var archive = new ArchiveFixtureClient();
        var polling = new PollingFixtureClient();
        await using var factory = new ReaderWebApplicationFactory(role);
        await using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            CompleteHealthReportTests.ConfigureSupplement(services);
            services.RemoveAll<IGameHostHealthClient>();
            services.AddSingleton<IGameHostHealthClient>(fixture);
            services.RemoveAll<IArchiveHealthClient>();
            services.AddSingleton<IArchiveHealthClient>(archive);
            services.RemoveAll<IPollingHealthClient>();
            services.AddSingleton<IPollingHealthClient>(polling);
        }));
        using var client = configured.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync(Path);
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        fixture.Reads.Should().Be(0);
        archive.Reads.Should().Be(0);
        polling.Reads.Should().Be(0);
        response.Content.Headers.ContentDisposition.Should().BeNull();
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task Operator_download_is_uncached_utf8_attachment_with_bounded_sanitized_output(bool unavailable, bool archiveUnavailable, bool pollingUnavailable)
    {
        var fixture = new FixtureClient { Unavailable = unavailable };
        var archive = new ArchiveFixtureClient { Unavailable = archiveUnavailable };
        var polling = new PollingFixtureClient { Unavailable = pollingUnavailable };
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        await using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            CompleteHealthReportTests.ConfigureSupplement(services);
            services.RemoveAll<IGameHostHealthClient>();
            services.AddSingleton<IGameHostHealthClient>(fixture);
            services.RemoveAll<IArchiveHealthClient>();
            services.AddSingleton<IArchiveHealthClient>(archive);
            services.RemoveAll<IPollingHealthClient>();
            services.AddSingleton<IPollingHealthClient>(polling);
        }));
        using var client = configured.CreateClient();
        using var response = await client.GetAsync(Path + "?filename=private-sentinel&host=private-sentinel");
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle().Which.Should().Be("nosniff");
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/plain");
        response.Content.Headers.ContentType.CharSet.Should().Be("utf-8");
        response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        response.Content.Headers.ContentDisposition.FileNameStar.Should().Be("goldsrcops-game-host-health.txt");
        response.Content.Headers.ContentLength.Should().BeLessThan(32768);
        text.Should().Contain("GoldSrcOps game-host health report").And.Contain("Snapshot evaluated:")
            .And.NotContain("private-sentinel").And.NotContain("Bearer").And.NotContain("Authorization");
        text.Should().Contain(unavailable ? "Snapshot: Unknown" : "Snapshot: Available");
        text.Should().Contain(archiveUnavailable ? "Archive observation: Unknown" : "Archive observation: Available");
        fixture.Reads.Should().Be(1);
        archive.Reads.Should().Be(1);
        polling.Reads.Should().Be(1);
        text.Should().Contain(pollingUnavailable ? "Polling observation: Unknown" : "Polling observation: Running");
        using var second = await client.GetAsync(Path + "/");
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        second.Headers.CacheControl!.NoStore.Should().BeTrue();
        fixture.Reads.Should().Be(2);
        archive.Reads.Should().Be(2);
        polling.Reads.Should().Be(2);
    }

    [Theory]
    [InlineData("game-host")]
    [InlineData("archive")]
    [InlineData("polling")]
    public async Task Caller_cancellation_reaches_upstream_without_an_unknown_report_or_retry(string waitSource)
    {
        var fixture = new WaitingClient();
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        await using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            CompleteHealthReportTests.ConfigureSupplement(services);
            services.RemoveAll<IGameHostHealthClient>();
            services.AddSingleton<IGameHostHealthClient>(waitSource is "game-host" ? fixture : new FixtureClient());
            services.RemoveAll<IArchiveHealthClient>();
            services.AddSingleton<IArchiveHealthClient>(waitSource is "archive" ? fixture : new ArchiveFixtureClient());
            services.RemoveAll<IPollingHealthClient>();
            services.AddSingleton<IPollingHealthClient>(waitSource is "polling" ? fixture : new PollingFixtureClient());
        }));
        using var client = configured.CreateClient();
        using var cancellation = new CancellationTokenSource();
        var request = client.GetAsync(Path, cancellation.Token);
        await fixture.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellation.Token);
        await cancellation.CancelAsync();
        var read = async () => await request.ConfigureAwait(false);
        await read.Should().ThrowAsync<OperationCanceledException>();
        // The request token is already cancelled; this assertion needs its own deadline.
        await fixture.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        fixture.Reads.Should().Be(1);
    }

    [Fact]
    public async Task All_three_reads_start_before_any_completes_and_share_request_cancellation()
    {
        var game = new WaitingClient();
        var archive = new WaitingClient();
        var polling = new WaitingClient();
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        await using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            CompleteHealthReportTests.ConfigureSupplement(services);
            services.RemoveAll<IGameHostHealthClient>();
            services.AddSingleton<IGameHostHealthClient>(game);
            services.RemoveAll<IArchiveHealthClient>();
            services.AddSingleton<IArchiveHealthClient>(archive);
            services.RemoveAll<IPollingHealthClient>();
            services.AddSingleton<IPollingHealthClient>(polling);
        }));
        using var client = configured.CreateClient();
        using var cancellation = new CancellationTokenSource();
        var request = client.GetAsync(Path, cancellation.Token);
        try
        {
            await Task.WhenAll(game.Started.Task, archive.Started.Task, polling.Started.Task).WaitAsync(TimeSpan.FromSeconds(10), cancellation.Token);
        }
        finally
        {
            await cancellation.CancelAsync();
        }

        var read = async () => await request.ConfigureAwait(false);
        await read.Should().ThrowAsync<OperationCanceledException>();
        // The cancelled request token must not replace the independent completion deadline.
        await Task.WhenAll(game.Cancelled.Task, archive.Cancelled.Task, polling.Cancelled.Task).WaitAsync(TimeSpan.FromSeconds(10));
        game.Reads.Should().Be(1);
        archive.Reads.Should().Be(1);
        polling.Reads.Should().Be(1);
    }

    private sealed class WaitingClient : IGameHostHealthClient, IArchiveHealthClient, IPollingHealthClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Reads { get; private set; }

        public async Task<GameHostHealthResponse> ReadAsync(CancellationToken cancellationToken = default)
        {
            await WaitAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("A cancelled request must not complete.");
        }

        async Task<ArchiveHealthResponse> IArchiveHealthClient.ReadAsync(CancellationToken cancellationToken)
        {
            await WaitAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("A cancelled request must not complete.");
        }

        async Task<PollingHealthResponse> IPollingHealthClient.ReadAsync(CancellationToken cancellationToken)
        {
            await WaitAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("A cancelled request must not complete.");
        }

        private async Task WaitAsync(CancellationToken cancellationToken)
        {
            Reads++;
            Started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    Cancelled.TrySetResult();
                }
            }
        }
    }

    private sealed class FixtureClient : IGameHostHealthClient
    {
        public bool Unavailable { get; init; }
        public int Reads { get; private set; }

        public Task<GameHostHealthResponse> ReadAsync(CancellationToken cancellationToken = default)
        {
            Reads++;
            return Unavailable ? throw new HttpRequestException("private-sentinel") :
                Task.FromResult(new GameHostHealthResponse(DateTimeOffset.UtcNow, [], [], new GameHostWarningsResponse("Available", DateTimeOffset.UtcNow, [])));
        }
    }

    private sealed class ArchiveFixtureClient : IArchiveHealthClient
    {
        public bool Unavailable { get; init; }
        public int Reads { get; private set; }

        public Task<ArchiveHealthResponse> ReadAsync(CancellationToken cancellationToken = default)
        {
            Reads++;
            var now = DateTimeOffset.UtcNow;
            return Unavailable ? throw new HttpRequestException("private-sentinel") :
                Task.FromResult(new ArchiveHealthResponse(now, "Available", now, HasScheduledSuccess: false, LastSuccessAtUtc: null, LastSuccessAgeSeconds: null,
                    "Unknown", WarningsObservedAtUtc: null, []));
        }
    }
    private sealed class PollingFixtureClient : IPollingHealthClient
    {
        public bool Unavailable { get; init; }
        public int Reads { get; private set; }

        public Task<PollingHealthResponse> ReadAsync(CancellationToken cancellationToken = default)
        {
            Reads++;
            var now = DateTimeOffset.UtcNow;
            return Unavailable ? throw new HttpRequestException("private-sentinel") :
                Task.FromResult(new PollingHealthResponse(now, "Running", now, now.AddSeconds(-6), now.AddSeconds(-5), 5, 370,
                    "Unknown", WarningsObservedAtUtc: null, []));
        }
    }

}
