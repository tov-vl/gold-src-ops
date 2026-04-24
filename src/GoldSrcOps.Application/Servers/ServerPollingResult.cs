namespace GoldSrcOps.Application.Servers;

public sealed record ServerPollingResult(
    int DueServers,
    int SuccessfulPolls,
    int FailedPolls);
