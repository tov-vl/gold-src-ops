using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GoldSrcOps.Infrastructure.Persistence.Outbox;

internal sealed class OutboxReplayRequestConfiguration
    : IEntityTypeConfiguration<OutboxReplayRequest>
{
    public void Configure(EntityTypeBuilder<OutboxReplayRequest> request)
    {
        request.ToTable("outbox_replay_requests", table =>
        {
            table.HasCheckConstraint(
                "CK_outbox_replay_requests_PayloadVersion",
                "\"PayloadVersion\" > 0");
            table.HasCheckConstraint(
                "CK_outbox_replay_requests_ReplayNumber",
                "\"ReplayNumber\" > 0");
            table.HasCheckConstraint(
                "CK_outbox_replay_requests_PreviousAttemptCount",
                "\"PreviousAttemptCount\" >= 0");
        });

        request.HasKey(x => x.Id);
        request.Property(x => x.Id).ValueGeneratedNever();
        request.Property(x => x.OutboxMessageId).IsRequired();
        request.Property(x => x.EventType)
            .HasMaxLength(OutboxMessage.MaxEventTypeLength)
            .IsRequired();
        request.Property(x => x.PayloadVersion).IsRequired();
        request.Property(x => x.AggregateType)
            .HasMaxLength(OutboxMessage.MaxAggregateTypeLength)
            .IsRequired();
        request.Property(x => x.AggregateId).IsRequired();
        request.Property(x => x.OccurredAtUtc).IsRequired();
        request.Property(x => x.RequestedBy)
            .HasMaxLength(OutboxReplayRequest.MaxRequestedByLength)
            .IsRequired();
        request.Property(x => x.RequestedAtUtc).IsRequired();
        request.Property(x => x.Reason)
            .HasMaxLength(OutboxReplayRequest.MaxReasonLength)
            .IsRequired();
        request.Property(x => x.ReplayNumber).IsRequired();
        request.Property(x => x.PreviousAttemptCount).IsRequired();
        request.Property(x => x.PreviousLastError)
            .HasMaxLength(OutboxMessage.MaxErrorLength);
        request.Property(x => x.NextAttemptAtUtc).IsRequired();

        request.HasIndex(x => new { x.OutboxMessageId, x.ReplayNumber })
            .HasDatabaseName("UX_outbox_replay_requests_Message_ReplayNumber")
            .IsUnique();
        request.HasIndex(x => new { x.OutboxMessageId, x.RequestedAtUtc })
            .HasDatabaseName("IX_outbox_replay_requests_Message_RequestedAtUtc");
    }
}
