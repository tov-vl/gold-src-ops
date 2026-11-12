using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using GoldSrcOps.Application.Common;
using GoldSrcOps.Application.Monitoring;
using GoldSrcOps.Contracts.Monitoring;
using GoldSrcOps.UnitTests.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace GoldSrcOps.UnitTests.Api;

public sealed class RetentionHealthEndpointTests
{
    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("reader", HttpStatusCode.Forbidden)]
    [InlineData("no-role", HttpStatusCode.Forbidden)]
    [InlineData("no-subject", HttpStatusCode.Unauthorized)]
    [InlineData("operator", HttpStatusCode.OK)]
    public async Task Authorization_precedes_all_retention_source_reads(string identity, HttpStatusCode expected)
    {
        var principal = identity switch
        {
            "anonymous" => TestApiPrincipal.Anonymous,
            "reader" => TestApiPrincipal.Reader(),
            "no-role" => TestApiPrincipal.WithoutRoles(),
            "no-subject" => TestApiPrincipal.WithoutSubject(),
            _ => TestApiPrincipal.Operator()
        };
        var source = new Mock<IRetentionHealthSource>(MockBehavior.Strict);
        if (expected == HttpStatusCode.OK)
        {
            source.Setup(static value => value.ReadAsync(RetentionHealthFixture.Now, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new RetentionHealthSnapshot(RetentionHealthFixture.Metrics(), RetentionHealthFixture.Rules()));
        }

        var clock = new Mock<IClock>();
        clock.SetupGet(static value => value.UtcNow).Returns(RetentionHealthFixture.Now);
        await using var factory = new GoldSrcOpsApiFactory(principal: principal, configureTestServices: services =>
        {
            services.RemoveAll<IRetentionHealthSource>();
            services.AddSingleton(source.Object);
            services.RemoveAll<IClock>();
            services.AddSingleton(clock.Object);
        });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/operator/retention-health");
        response.StatusCode.Should().Be(expected);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        if (expected == HttpStatusCode.OK)
        {
            response.Headers.CacheControl!.NoStore.Should().BeTrue();
            var value = await response.Content.ReadFromJsonAsync<RetentionHealthResponse>();
            value!.ObservationState.Should().Be("Running");
            value.CompletionAgeSeconds.Should().Be(5);
            value.Warnings.Should().HaveCount(3);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            document.RootElement.EnumerateObject().Select(static item => item.Name).Should().BeEquivalentTo(
                "evaluatedAtUtc", "observationState", "observedAtUtc", "lastCycleStartedAtUtc", "lastCycleFinishedAtUtc", "completionAgeSeconds", "cycleBudgetSeconds", "warningsState", "warningsObservedAtUtc", "warnings", "lastSuccessfulCycleAtUtc", "lastFailureAtUtc", "lastCycleDurationSeconds", "consecutiveFailures", "inProgress");
            source.Verify(static value => value.ReadAsync(RetentionHealthFixture.Now, It.IsAny<CancellationToken>()), Times.Once);
        }

        source.VerifyNoOtherCalls();
    }
}
