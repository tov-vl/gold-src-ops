namespace GoldSrcOps.Application.Alerts;

public interface IOutboxWriter
{
    void Add(IncidentAlertEventV1 alert);
}
