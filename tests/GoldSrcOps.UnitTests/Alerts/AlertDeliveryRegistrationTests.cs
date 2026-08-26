using AwesomeAssertions;
using GoldSrcOps.Application.Alerts;
using GoldSrcOps.Infrastructure;
using GoldSrcOps.Infrastructure.Alerts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;

namespace GoldSrcOps.UnitTests.Alerts;

public sealed class AlertDeliveryRegistrationTests
{
    [Fact]
    public void AddInfrastructure_registers_the_dispatcher_only_when_delivery_is_enabled()
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ConnectionStrings:GoldSrcOps"] = "Host=localhost;Database=goldsrcops",
            ["AlertDelivery:Enabled"] = "true",
            ["AlertDelivery:WebhookUrl"] = "https://webhook.example.test/alerts"
        });

        services.AddInfrastructure(configuration, CreateEnvironment(Environments.Production));

        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IAlertDeliveryChannel) &&
            descriptor.ImplementationType == typeof(HttpWebhookAlertDeliveryChannel));
        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(AlertDispatcher));
        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationType == typeof(AlertDispatchBackgroundService));
    }

    [Fact]
    public void AddInfrastructure_omits_delivery_services_when_delivery_is_disabled()
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ConnectionStrings:GoldSrcOps"] = "Host=localhost;Database=goldsrcops"
        });

        services.AddInfrastructure(configuration, CreateEnvironment(Environments.Production));

        services.Should().NotContain(descriptor =>
            descriptor.ServiceType == typeof(IAlertDeliveryChannel));
        services.Should().NotContain(descriptor =>
            descriptor.ServiceType == typeof(AlertDispatcher));
        services.Should().NotContain(descriptor =>
            descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationType == typeof(AlertDispatchBackgroundService));
    }

    [Fact]
    public void AddInfrastructure_does_not_echo_an_invalid_authorization_value()
    {
        const string secretMarker = "deployment-secret-marker";
        var services = new ServiceCollection();
        var configuration = CreateConfiguration(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ConnectionStrings:GoldSrcOps"] = "Host=localhost;Database=goldsrcops",
            ["AlertDelivery:Enabled"] = "true",
            ["AlertDelivery:WebhookUrl"] = "https://webhook.example.test/alerts",
            ["AlertDelivery:Authorization"] = $"invalid\r\n{secretMarker}"
        });

        var act = () => services.AddInfrastructure(
            configuration,
            CreateEnvironment(Environments.Production));

        act.Should().Throw<ArgumentException>()
            .WithMessage("*authorization value is invalid*")
            .Which.Message.Should().NotContain(secretMarker);
    }

    private static IConfiguration CreateConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

    private static IHostEnvironment CreateEnvironment(string environmentName)
    {
        var environment = new Mock<IHostEnvironment>(MockBehavior.Strict);
        environment.SetupGet(x => x.EnvironmentName).Returns(environmentName);
        return environment.Object;
    }
}
