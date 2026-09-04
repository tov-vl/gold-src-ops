namespace GoldSrcOps.Application.Availability;

public sealed record AvailabilityOutcomeCounts(
    int Good,
    int DnsError,
    int ConnectError,
    int TlsError,
    int Timeout,
    int Redirect,
    int HttpError,
    int MonitorError,
    int Missing);
