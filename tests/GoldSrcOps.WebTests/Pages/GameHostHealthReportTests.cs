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
        await using var factory = new ReaderWebApplicationFactory(role);
        await using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IGameHostHealthClient>();
            services.AddSingleton<IGameHostHealthClient>(fixture);
        }));
        using var client = configured.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync(Path);
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        fixture.Reads.Should().Be(0);
        response.Content.Headers.ContentDisposition.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Operator_download_is_uncached_utf8_attachment_with_bounded_sanitized_output(bool unavailable)
    {
        var fixture = new FixtureClient { Unavailable = unavailable };
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        await using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IGameHostHealthClient>();
            services.AddSingleton<IGameHostHealthClient>(fixture);
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
        fixture.Reads.Should().Be(1);
        using var second = await client.GetAsync(Path + "/");
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        second.Headers.CacheControl!.NoStore.Should().BeTrue();
        fixture.Reads.Should().Be(2);
    }

    [Fact]
    public async Task Caller_cancellation_reaches_upstream_without_an_unknown_report_or_retry()
    {
        var fixture = new WaitingClient();
        await using var factory = new ReaderWebApplicationFactory(WebSecurity.OperatorRole);
        await using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IGameHostHealthClient>();
            services.AddSingleton<IGameHostHealthClient>(fixture);
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

    private sealed class WaitingClient : IGameHostHealthClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Reads { get; private set; }

        public async Task<GameHostHealthResponse> ReadAsync(CancellationToken cancellationToken = default)
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

            throw new InvalidOperationException("A cancelled request must not complete.");
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
}
