namespace GoldSrcOps.Application.Availability;

public enum ProbeFailureKind
{
    Dns,
    Connect,
    Tls,
    Timeout,
    Monitor,
}
