namespace GoldSrcOps.Application.Alerts;

public interface IAlertRetryDelayProvider
{
    TimeSpan GetDelay(int attemptCount);
}
