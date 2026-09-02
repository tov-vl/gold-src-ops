using AwesomeAssertions;
using GoldSrcOps.Api.Hosting;
using Microsoft.Extensions.Configuration;
using OpenTelemetry.Exporter;

namespace GoldSrcOps.UnitTests.Api;

public sealed class OtlpMetricsOptionsTests
{
    [Fact]
    public void FromConfiguration_uses_disabled_safe_defaults_when_section_is_absent()
    {
        var configuration = new ConfigurationBuilder().Build();

        var options = OtlpMetricsOptions.FromConfiguration(configuration);

        options.Enabled.Should().BeFalse();
        options.Endpoint.Should().BeNull();
        options.Protocol.Should().Be(OtlpExportProtocol.Grpc);
        options.ExportIntervalMilliseconds.Should().Be(60_000);
        options.ExportTimeoutMilliseconds.Should().Be(30_000);
    }

    [Theory]
    [InlineData("grpc", "http://otel-collector:4317", OtlpExportProtocol.Grpc)]
    [InlineData(" HTTP/PROTOBUF ", "http://otel-collector:4318", OtlpExportProtocol.HttpProtobuf)]
    public void FromConfiguration_reads_valid_values(
        string protocol,
        string endpoint,
        OtlpExportProtocol expectedProtocol)
    {
        var configuration = CreateConfiguration(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Telemetry:Otlp:Enabled"] = "true",
            ["Telemetry:Otlp:Endpoint"] = endpoint,
            ["Telemetry:Otlp:Protocol"] = protocol,
            ["Telemetry:Otlp:ExportIntervalMilliseconds"] = "15000",
            ["Telemetry:Otlp:ExportTimeoutMilliseconds"] = "5000"
        });

        var options = OtlpMetricsOptions.FromConfiguration(configuration);

        options.Enabled.Should().BeTrue();
        options.Endpoint.Should().Be(new Uri(endpoint));
        options.Protocol.Should().Be(expectedProtocol);
        options.ExportIntervalMilliseconds.Should().Be(15_000);
        options.ExportTimeoutMilliseconds.Should().Be(5_000);
    }

    [Theory]
    [InlineData("Telemetry:Otlp:Enabled", "sometimes")]
    [InlineData("Telemetry:Otlp:Endpoint", "collector:4317")]
    [InlineData("Telemetry:Otlp:Endpoint", "ftp://collector:4317")]
    [InlineData("Telemetry:Otlp:Endpoint", "http://user:password@collector:4317")]
    [InlineData("Telemetry:Otlp:Endpoint", "http://collector:4317?token=secret")]
    [InlineData("Telemetry:Otlp:Protocol", "json")]
    [InlineData("Telemetry:Otlp:ExportIntervalMilliseconds", "999")]
    [InlineData("Telemetry:Otlp:ExportTimeoutMilliseconds", "99")]
    public void FromConfiguration_rejects_invalid_values(string key, string value)
    {
        var configuration = CreateConfiguration(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [key] = value
        });

        var act = () => OtlpMetricsOptions.FromConfiguration(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{key}*");
    }

    [Fact]
    public void FromConfiguration_requires_endpoint_when_export_is_enabled()
    {
        var configuration = CreateConfiguration(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Telemetry:Otlp:Enabled"] = "true"
        });

        var act = () => OtlpMetricsOptions.FromConfiguration(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Telemetry:Otlp:Endpoint*");
    }

    [Fact]
    public void FromConfiguration_rejects_timeout_longer_than_interval()
    {
        var configuration = CreateConfiguration(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Telemetry:Otlp:ExportIntervalMilliseconds"] = "1000",
            ["Telemetry:Otlp:ExportTimeoutMilliseconds"] = "1001"
        });

        var act = () => OtlpMetricsOptions.FromConfiguration(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ExportTimeoutMilliseconds*ExportIntervalMilliseconds*");
    }

    private static IConfiguration CreateConfiguration(
        IReadOnlyDictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
}
