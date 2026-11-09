using System.Diagnostics.Metrics;
using GoldSrcOps.Application.Monitoring;
using GoldSrcOps.Application.Telemetry;
using Microsoft.Extensions.Hosting;

namespace GoldSrcOps.Infrastructure.Monitoring;

internal sealed class CredentialExpiryMetrics : IHostedService, IDisposable
{
    private static readonly string[] States = ["Unknown", "Recorded", "Soon", "Critical", "Expired"];
    private readonly Meter _meter;

    public CredentialExpiryMetrics(CredentialExpiryService service)
    {
        _meter = new Meter(new MeterOptions(GoldSrcOpsMetrics.MeterName) { Scope = service });
        _meter.CreateObservableGauge("goldsrcops.credential_expiry.observed_timestamp",
            () => (service.Read(CancellationToken.None).EvaluatedAtUtc - DateTimeOffset.UnixEpoch).TotalSeconds, unit: "s");
        _meter.CreateObservableGauge("goldsrcops.credential_expiry.state", () =>
        {
            var snapshot = service.Read(CancellationToken.None);
            return snapshot.Entries.SelectMany(entry => States.Select(state => new Measurement<int>(
                string.Equals(entry.State, state, StringComparison.Ordinal) ? 1 : 0,
                new KeyValuePair<string, object?>("purpose", entry.Key),
                new KeyValuePair<string, object?>("state", state))));
        });
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => _meter.Dispose();
}
