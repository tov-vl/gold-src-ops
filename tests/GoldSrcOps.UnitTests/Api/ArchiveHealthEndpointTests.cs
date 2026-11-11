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

public sealed class ArchiveHealthEndpointTests
{
    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("reader", HttpStatusCode.Forbidden)]
    [InlineData("no-role", HttpStatusCode.Forbidden)]
    [InlineData("no-subject", HttpStatusCode.Unauthorized)]
    [InlineData("operator", HttpStatusCode.OK)]
    public async Task Authorization_precedes_all_archive_source_reads(string identity, HttpStatusCode expected)
    {
        var principal = identity switch
        {
            "anonymous" => TestApiPrincipal.Anonymous,
            "reader" => TestApiPrincipal.Reader(),
            "no-role" => TestApiPrincipal.WithoutRoles(),
            "no-subject" => TestApiPrincipal.WithoutSubject(),
            _ => TestApiPrincipal.Operator()
        };
        var source = new Mock<IArchiveHealthSource>(MockBehavior.Strict);
        if (expected == HttpStatusCode.OK)
        {
            source.Setup(static value => value.ReadAsync(ArchiveHealthFixture.Now, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ArchiveHealthSnapshot(ArchiveHealthFixture.Metrics(600), ArchiveHealthFixture.Rules()));
        }

        var clock = new Mock<IClock>();
        clock.SetupGet(static value => value.UtcNow).Returns(ArchiveHealthFixture.Now);
        await using var factory = new GoldSrcOpsApiFactory(principal: principal, configureTestServices: services =>
        {
            services.RemoveAll<IArchiveHealthSource>();
            services.AddSingleton(source.Object);
            services.RemoveAll<IClock>();
            services.AddSingleton(clock.Object);
        });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/operator/archive-health");
        response.StatusCode.Should().Be(expected);
        if (expected == HttpStatusCode.OK)
        {
            response.Headers.CacheControl!.NoStore.Should().BeTrue();
            var value = await response.Content.ReadFromJsonAsync<ArchiveHealthResponse>();
            value!.ObservationState.Should().Be("Available");
            value.LastSuccessAgeSeconds.Should().Be(10800);
            value.Warnings.Should().HaveCount(4);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            document.RootElement.EnumerateObject().Select(static item => item.Name).Should().BeEquivalentTo(
                "evaluatedAtUtc", "observationState", "observedAtUtc", "hasScheduledSuccess", "lastSuccessAtUtc", "lastSuccessAgeSeconds", "warningsState", "warningsObservedAtUtc", "warnings");
            source.Verify(static value => value.ReadAsync(ArchiveHealthFixture.Now, It.IsAny<CancellationToken>()), Times.Once);
        }

        source.VerifyNoOtherCalls();
    }
}
