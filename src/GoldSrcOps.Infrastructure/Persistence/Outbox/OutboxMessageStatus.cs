namespace GoldSrcOps.Infrastructure.Persistence.Outbox;

internal enum OutboxMessageStatus
{
    Pending,
    Processing,
    Processed,
    DeadLetter
}
