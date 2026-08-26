using AwesomeAssertions;
using GoldSrcOps.Infrastructure.Alerts;
using Microsoft.Extensions.Configuration;

namespace GoldSrcOps.UnitTests.Alerts;

public sealed class AlertDeliveryOptionsTests
{
    [Fact]
    public void FromConfiguration_uses_disabled_safe_defaults_when_section_is_absent()
    {
        var configuration = new ConfigurationBuilder().Build();

        var result = AlertDeliveryOptions.FromConfiguration(
            configuration,
            allowHttpEndpoint: false);

        result.Enabled.Should().BeFalse();
        result.WebhookEndpoint.Should().BeNull();
        result.MaxConcurrency.Should().Be(4);
        result.ClaimTimeout.Should().Be(TimeSpan.FromSeconds(30));
        result.RequestTimeout.Should().Be(TimeSpan.FromSeconds(10));
        result.MaxAttempts.Should().Be(8);
        result.ProcessedRetentionPeriod.Should().Be(TimeSpan.FromDays(30));
        result.CleanupBatchSize.Should().Be(1_000);
    }

    [Fact]
    public void FromConfiguration_reads_valid_development_values()
    {
        var configuration = CreateConfiguration(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["AlertDelivery:Enabled"] = "true",
            ["AlertDelivery:WebhookUrl"] = "http://127.0.0.1:8080/alerts",
            ["AlertDelivery:Authorization"] = "Bearer deployment-secret",
            ["AlertDelivery:LoopDelayMilliseconds"] = "250",
            ["AlertDelivery:MaxConcurrency"] = "2",
            ["AlertDelivery:ClaimTimeoutSeconds"] = "45",
            ["AlertDelivery:RecoveryIntervalSeconds"] = "15",
            ["AlertDelivery:RequestTimeoutSeconds"] = "12",
            ["AlertDelivery:MaxAttempts"] = "6",
            ["AlertDelivery:BaseRetryDelaySeconds"] = "3",
            ["AlertDelivery:MaximumRetryDelaySeconds"] = "120",
            ["AlertDelivery:MetricsIntervalSeconds"] = "20",
            ["AlertDelivery:ProcessedRetentionDays"] = "14",
            ["AlertDelivery:CleanupIntervalSeconds"] = "600",
            ["AlertDelivery:CleanupBatchSize"] = "250"
        });

        var result = AlertDeliveryOptions.FromConfiguration(
            configuration,
            allowHttpEndpoint: true);

        result.Enabled.Should().BeTrue();
        result.WebhookEndpoint.Should().Be(new Uri("http://127.0.0.1:8080/alerts"));
        result.Authorization.Should().Be("Bearer deployment-secret");
        result.LoopDelay.Should().Be(TimeSpan.FromMilliseconds(250));
        result.MaxConcurrency.Should().Be(2);
        result.ClaimTimeout.Should().Be(TimeSpan.FromSeconds(45));
        result.RecoveryInterval.Should().Be(TimeSpan.FromSeconds(15));
        result.RequestTimeout.Should().Be(TimeSpan.FromSeconds(12));
        result.MaxAttempts.Should().Be(6);
        result.BaseRetryDelay.Should().Be(TimeSpan.FromSeconds(3));
        result.MaximumRetryDelay.Should().Be(TimeSpan.FromSeconds(120));
        result.MetricsInterval.Should().Be(TimeSpan.FromSeconds(20));
        result.ProcessedRetentionPeriod.Should().Be(TimeSpan.FromDays(14));
        result.CleanupInterval.Should().Be(TimeSpan.FromMinutes(10));
        result.CleanupBatchSize.Should().Be(250);
    }

    [Fact]
    public void FromConfiguration_requires_a_webhook_when_delivery_is_enabled()
    {
        var configuration = CreateConfiguration(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["AlertDelivery:Enabled"] = "true"
        });

        var act = () => AlertDeliveryOptions.FromConfiguration(
            configuration,
            allowHttpEndpoint: false);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AlertDelivery:WebhookUrl*");
    }

    [Fact]
    public void FromConfiguration_rejects_http_webhooks_outside_development()
    {
        var configuration = CreateConfiguration(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["AlertDelivery:WebhookUrl"] = "http://webhook.example.test/alerts"
        });

        var act = () => AlertDeliveryOptions.FromConfiguration(
            configuration,
            allowHttpEndpoint: false);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AlertDelivery:WebhookUrl*HTTPS*");
    }

    [Theory]
    [InlineData("Enabled", "sometimes")]
    [InlineData("LoopDelayMilliseconds", "9")]
    [InlineData("MaxConcurrency", "0")]
    [InlineData("ClaimTimeoutSeconds", "1")]
    [InlineData("MaxAttempts", "101")]
    [InlineData("ProcessedRetentionDays", "0")]
    [InlineData("CleanupBatchSize", "10001")]
    public void FromConfiguration_rejects_invalid_scalar_values(string key, string value)
    {
        var configuration = CreateConfiguration(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [$"AlertDelivery:{key}"] = value
        });

        var act = () => AlertDeliveryOptions.FromConfiguration(
            configuration,
            allowHttpEndpoint: false);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*AlertDelivery:{key}*");
    }

    [Theory]
    [InlineData("ClaimTimeoutSeconds", "10", "RequestTimeoutSeconds", "10")]
    [InlineData("BaseRetryDelaySeconds", "60", "MaximumRetryDelaySeconds", "30")]
    public void FromConfiguration_rejects_invalid_relationships(
        string firstKey,
        string firstValue,
        string secondKey,
        string secondValue)
    {
        var configuration = CreateConfiguration(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [$"AlertDelivery:{firstKey}"] = firstValue,
            [$"AlertDelivery:{secondKey}"] = secondValue
        });

        var act = () => AlertDeliveryOptions.FromConfiguration(
            configuration,
            allowHttpEndpoint: false);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AlertDelivery:*");
    }

    private static IConfiguration CreateConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
}
