namespace GoldSrcOps.Application.Alerts;

public static class IncidentAlertEvents
{
    public const string AggregateType = "availability-incident";
    public const string ServerUnavailable = "server.availability.unavailable";
    public const string ServerRecovered = "server.availability.recovered";
}
