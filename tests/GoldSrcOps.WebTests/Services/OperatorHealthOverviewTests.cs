using System.Text.Json;
using AwesomeAssertions;
using GoldSrcOps.Contracts.Alerts;
using GoldSrcOps.Contracts.Monitoring;
using GoldSrcOps.Contracts.ProviderDelivery;
using GoldSrcOps.Web.Services;

namespace GoldSrcOps.WebTests.Services;

public sealed class OperatorHealthOverviewTests
{
    private static DateTimeOffset Now => OperatorHealthOverviewFixtures.Now;

    [Fact]
    public void Fresh_sources_keep_independent_states_times_and_recorded_deadline_semantics()
    {
        var overview = Create();
        overview.Sources.Select(static item => item.Key).Should().Equal("game-host", "archive", "polling", "credentials");
        overview.Sources.Should().OnlyContain(static item => item.SnapshotState == "Available");
        overview.Sources[0].Facts.Should().Contain(static item => item.Value.StartsWith("Observed (no CPU threshold)", StringComparison.Ordinal));
        overview.Sources[0].Rules!.Items.Should().ContainSingle().Which.Name.Should().Be("GameHostMemoryLow");
        overview.Sources[1].Rules!.Items.Should().ContainSingle().Which.Name.Should().Be("ArchiveScheduleDelayed");
        overview.Sources[1].Facts.Should().Contain(new OperatorHealthOverview.Fact("Success age (seconds)", "14400"));
        overview.Sources[2].Facts.Should().Contain(new OperatorHealthOverview.Fact("Worker state", "Running"));
        overview.Sources[2].Rules!.Items.Should().BeEmpty();
        overview.Sources[2].Rules!.ObservedAtUtc.Should().Be(Now.AddSeconds(-6));
        overview.Sources[0].Rules!.ObservedAtUtc.Should().Be(Now.AddSeconds(-5));
        overview.Sources[3].Facts.Select(static item => item.Value).Should().Equal(
            "Recorded deadline beyond 30 days", "Due within 30 days", "Due within 7 days", "Recorded minute reached (conservative)");
        overview.Sources[3].Rules.Should().BeNull();
        JsonSerializer.Serialize(overview).Should().NotContain("private-sentinel");
    }

    [Theory]
    [InlineData(0, -180, "Available")]
    [InlineData(0, -181, "Stale")]
    [InlineData(1, -60, "Available")]
    [InlineData(1, -61, "Stale")]
    [InlineData(2, -60, "Available")]
    [InlineData(2, -61, "Stale")]
    [InlineData(3, -180, "Available")]
    [InlineData(3, -181, "Stale")]
    [InlineData(0, 1, "Unknown")]
    [InlineData(1, 1, "Unknown")]
    [InlineData(2, 1, "Unknown")]
    [InlineData(3, 1, "Unknown")]
    public void Source_freshness_uses_its_existing_boundary(int index, int offset, string expected)
    {
        var evaluated = Now.AddSeconds(offset);
        var game = OperatorHealthOverviewFixtures.GameHost(Now);
        var archive = OperatorHealthOverviewFixtures.Archive(Now);
        var polling = OperatorHealthOverviewFixtures.Polling(Now);
        var credentials = OperatorHealthOverviewFixtures.Credentials(Now);
        var overview = OperatorHealthOverview.Create(index == 0 ? game with { EvaluatedAtUtc = evaluated } : game,
            index == 1 ? archive with { EvaluatedAtUtc = evaluated } : archive,
            index == 2 ? polling with { EvaluatedAtUtc = evaluated } : polling,
            index == 3 ? credentials with { EvaluatedAtUtc = evaluated } : credentials, Now);
        overview.Sources[index].SnapshotState.Should().Be(expected);
        overview.Sources.Where((_, position) => position != index).Should().OnlyContain(static source => source.SnapshotState == "Available");
        if (expected is "Unknown")
        {
            overview.Sources[index].EvaluatedAtUtc.Should().BeNull();
        }
    }

    public static TheoryData<int> FailureMasks
    {
        get
        {
            var data = new TheoryData<int>();
            foreach (var mask in Enumerable.Range(0, 64))
            {
                data.Add(mask);
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(FailureMasks))]
    public async Task Every_partial_failure_combination_reads_each_source_once(int unavailableMask)
    {
        var client = new FixtureClient(unavailableMask);
        var overview = await ReadAsync(client);
        client.Reads.Should().Equal(1, 1, 1, 1, 1, 1);
        for (var index = 0; index < 6; index++)
        {
            overview.Sources[index].SnapshotState.Should().Be((unavailableMask & (1 << index)) == 0 ? "Available" : "Unknown");
        }
    }

    [Fact]
    public void Malformed_duplicate_and_oversized_fields_never_reflect_provider_material()
    {
        const string sentinel = "private-sentinel\r\nAuthorization: secret";
        var game = OperatorHealthOverviewFixtures.GameHost(Now) with
        {
            Components = [.. Enumerable.Repeat(new GameHostHealthComponentResponse(sentinel, sentinel, Now), 100)],
            Warnings = new GameHostWarningsResponse("Available", Now,
                [new GameHostWarningResponse("GameHostMemoryLow", sentinel, "Firing", sentinel), new GameHostWarningResponse("GameHostMemoryLow", sentinel, "Pending", sentinel)])
        };
        var archive = OperatorHealthOverviewFixtures.Archive(Now) with { Warnings = [.. Enumerable.Repeat(new ArchiveWarningResponse(sentinel, sentinel), 100)] };
        var polling = OperatorHealthOverviewFixtures.Polling(Now) with { ObservationState = sentinel, WarningsState = sentinel };
        var credentials = OperatorHealthOverviewFixtures.Credentials(Now) with { Entries = [.. Enumerable.Repeat(new CredentialExpiryEntryResponse(sentinel, sentinel, Now, Now, 0), 100)] };
        var result = OperatorHealthOverview.Create(game, archive, polling, credentials, Now);
        JsonSerializer.Serialize(result).Should().NotContain("private-sentinel").And.NotContain("Authorization").And.NotContain("secret");
        result.Sources.Take(3).Should().OnlyContain(static item => item.Rules!.State == "Unknown" && item.Rules.Items.Count == 0);
        result.Sources[0].Facts.Should().OnlyContain(static item => item.Value.StartsWith("Unknown;", StringComparison.Ordinal));
        result.Sources[3].Facts.Should().OnlyContain(static item => item.Value == "Unknown");
        JsonSerializer.SerializeToUtf8Bytes(result).Length.Should().BeLessThan(16384);
    }

    [Fact]
    public void Old_rule_evaluation_and_observation_do_not_hide_each_other()
    {
        var game = OperatorHealthOverviewFixtures.GameHost(Now) with { Warnings = new GameHostWarningsResponse("Available", Now.AddSeconds(-61), []) };
        var archive = OperatorHealthOverviewFixtures.Archive(Now) with { ObservedAtUtc = Now.AddMinutes(-31) };
        var polling = OperatorHealthOverviewFixtures.Polling(Now) with { LastCycleFinishedAtUtc = Now.AddSeconds(-371), CompletionAgeSeconds = 371 };
        var result = OperatorHealthOverview.Create(game, archive, polling, OperatorHealthOverviewFixtures.Credentials(Now), Now);
        result.Sources[0].Rules!.State.Should().Be("Stale");
        result.Sources[0].Rules!.Items.Should().BeEmpty();
        result.Sources[1].Facts.Should().Contain(new OperatorHealthOverview.Fact("Observation", "Stale"));
        result.Sources[1].Rules!.Items.Should().ContainSingle();
        result.Sources[2].Facts.Should().Contain(new OperatorHealthOverview.Fact("Worker state", "Stale"));
        result.Sources[2].Rules!.State.Should().Be("Available");
    }

    [Fact]
    public async Task Six_reads_start_together_and_caller_cancellation_reaches_all_without_retry()
    {
        var client = new FixtureClient(wait: true);
        using var cancellation = new CancellationTokenSource();
        var request = ReadAsync(client, cancellation.Token);
        try
        {
            await client.AllStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellation.Token);
        }
        finally
        {
            await cancellation.CancelAsync();
        }

        var read = async () => await request;
        await read.Should().ThrowAsync<OperationCanceledException>();
        client.Reads.Should().Equal(1, 1, 1, 1, 1, 1);
        client.Cancelled.Should().Be(6);
    }

    [Fact]
    public async Task Already_cancelled_request_does_not_start_any_source()
    {
        var client = new FixtureClient();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var token = cancellation.Token;
        var read = async () => await ReadAsync(client, token);
        await read.Should().ThrowAsync<OperationCanceledException>();
        client.Reads.Should().Equal(0, 0, 0, 0, 0, 0);
    }

    private static OperatorHealthOverview Create() => OperatorHealthOverview.Create(OperatorHealthOverviewFixtures.GameHost(Now),
        OperatorHealthOverviewFixtures.Archive(Now), OperatorHealthOverviewFixtures.Polling(Now), OperatorHealthOverviewFixtures.Credentials(Now), Now);

    private static Task<OperatorHealthOverview> ReadAsync(FixtureClient client, CancellationToken token = default) =>
        OperatorHealthOverview.ReadAsync(client, client, client, client, client, new OperatorHealthOverviewFixtures.Clock(), token);

    internal sealed class FixtureClient(int unavailableMask = 0, bool wait = false) : IGameHostHealthClient, IArchiveHealthClient, IPollingHealthClient, ICredentialExpiryClient, IIncidentDeliveryHealthClient
    {
        public int[] Reads { get; } = new int[6];
        public bool IsReceiverConfigured { get; init; } = true;
        public TaskCompletionSource AllStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started;
        private int _cancelled;
        public int Cancelled => _cancelled;

        Task<GameHostHealthResponse> IGameHostHealthClient.ReadAsync(CancellationToken cancellationToken) => ReadAsync(0, OperatorHealthOverviewFixtures.GameHost, cancellationToken);
        Task<ArchiveHealthResponse> IArchiveHealthClient.ReadAsync(CancellationToken cancellationToken) => ReadAsync(1, OperatorHealthOverviewFixtures.Archive, cancellationToken);
        Task<PollingHealthResponse> IPollingHealthClient.ReadAsync(CancellationToken cancellationToken) => ReadAsync(2, OperatorHealthOverviewFixtures.Polling, cancellationToken);
        Task<CredentialExpiryResponse> ICredentialExpiryClient.ReadAsync(CancellationToken cancellationToken) => ReadAsync(3, OperatorHealthOverviewFixtures.Credentials, cancellationToken);
        public Task<AlertDeliveryStatusResponse> ReadOutboxAsync(CancellationToken cancellationToken) => ReadAsync(4, OperatorHealthOverviewFixtures.Outbox, cancellationToken);
        public Task<ProviderDeliveryStatusResponse> ReadReceiverAsync(CancellationToken cancellationToken) => ReadAsync(5, OperatorHealthOverviewFixtures.Receiver, cancellationToken);

        private async Task<T> ReadAsync<T>(int index, Func<DateTimeOffset, T> factory, CancellationToken cancellationToken)
        {
            Reads[index]++;
            if (Interlocked.Increment(ref _started) == 6)
            {
                AllStarted.TrySetResult();
            }

            if (wait)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        if (Interlocked.Increment(ref _cancelled) == 6)
                        {
                            AllCancelled.TrySetResult();
                        }
                    }
                }
            }

            if ((unavailableMask & (1 << index)) != 0)
            {
                throw index switch
                {
                    0 => new HttpRequestException("private-sentinel"),
                    1 => new TaskCanceledException("private-sentinel"),
                    2 => new JsonException("private-sentinel"),
                    4 => new OperationCanceledException("private-sentinel"),
                    _ => new InvalidDataException("private-sentinel")
                };
            }

            return factory(Now);
        }
    }
}
