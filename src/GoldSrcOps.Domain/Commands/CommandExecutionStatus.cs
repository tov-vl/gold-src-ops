namespace GoldSrcOps.Domain.Commands;

public enum CommandExecutionStatus
{
    Pending = 1,
    Running = 2,
    Succeeded = 3,
    Failed = 4
}
