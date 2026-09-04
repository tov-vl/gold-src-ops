namespace GoldSrcOps.Application.Availability;

public enum AvailabilityOutcome
{
    Good,
    DnsError,
    ConnectError,
    TlsError,
    Timeout,
    Redirect,
    HttpError,
    MonitorError,
    Missing,
}
