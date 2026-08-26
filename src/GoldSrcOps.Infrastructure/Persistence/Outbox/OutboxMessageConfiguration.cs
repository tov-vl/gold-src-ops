using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

namespace GoldSrcOps.Infrastructure.Persistence.Outbox;

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> message)
    {
        message.ToTable("outbox_messages", table =>
        {
            table.HasCheckConstraint(
                "CK_outbox_messages_PayloadVersion",
                "\"PayloadVersion\" > 0");
            table.HasCheckConstraint(
                "CK_outbox_messages_AttemptCount",
                "\"AttemptCount\" >= 0");
            table.HasCheckConstraint(
                "CK_outbox_messages_ReplayCount",
                "\"ReplayCount\" >= 0");
            table.HasCheckConstraint(
                "CK_outbox_messages_DeadLetteredAtUtc",
                "\"DeadLetteredAtUtc\" IS NULL OR \"Status\" = 'DeadLetter'");
            table.HasCheckConstraint(
                "CK_outbox_messages_StatusFields",
                """
                ("Status" = 'Pending'
                    AND "ClaimId" IS NULL
                    AND "ClaimedAtUtc" IS NULL
                    AND "ProcessedAtUtc" IS NULL)
                OR ("Status" = 'Processing'
                    AND "ClaimId" IS NOT NULL
                    AND "ClaimedAtUtc" IS NOT NULL
                    AND "ProcessedAtUtc" IS NULL)
                OR ("Status" = 'Processed'
                    AND "ClaimId" IS NULL
                    AND "ClaimedAtUtc" IS NULL
                    AND "ProcessedAtUtc" IS NOT NULL)
                OR ("Status" = 'DeadLetter'
                    AND "ClaimId" IS NULL
                    AND "ClaimedAtUtc" IS NULL
                    AND "ProcessedAtUtc" IS NULL)
                """);
        });

        message.HasKey(x => x.Id);
        message.Property(x => x.Id).ValueGeneratedNever();
        message.Property(x => x.EventType)
            .HasMaxLength(OutboxMessage.MaxEventTypeLength)
            .IsRequired();
        message.Property(x => x.PayloadVersion).IsRequired();
        message.Property(x => x.AggregateType)
            .HasMaxLength(OutboxMessage.MaxAggregateTypeLength)
            .IsRequired();
        message.Property(x => x.AggregateId).IsRequired();
        message.Property(x => x.OccurredAtUtc).IsRequired();
        message.Property(x => x.Payload).HasColumnType("jsonb").IsRequired();
        message.Property(x => x.Status)
            .HasConversion<string>()
            .HasMaxLength(OutboxMessage.MaxStatusLength)
            .IsRequired();
        message.Property(x => x.AttemptCount).IsRequired();
        message.Property(x => x.NextAttemptAtUtc).IsRequired();
        message.Property(x => x.LastError).HasMaxLength(OutboxMessage.MaxErrorLength);
        message.Property(x => x.ReplayCount).HasDefaultValue(0).IsRequired();

        message.HasIndex(x => new { x.EventType, x.AggregateId })
            .HasDatabaseName("UX_outbox_messages_EventType_AggregateId")
            .IsUnique();
        message.HasIndex(x => new
        {
            x.AggregateType,
            x.AggregateId,
            x.OccurredAtUtc,
            x.Id
        })
            .HasDatabaseName("IX_outbox_messages_active_aggregate_order")
            .HasFilter("\"Status\" IN ('Pending', 'Processing')");
        message.HasIndex(x => new
        {
            x.Status,
            x.NextAttemptAtUtc,
            x.OccurredAtUtc,
            x.Id
        })
            .HasDatabaseName("IX_outbox_messages_pending_claim")
            .HasFilter("\"Status\" = 'Pending'");
        message.HasIndex(x => new { x.Status, x.ClaimedAtUtc })
            .HasDatabaseName("IX_outbox_messages_processing_recovery")
            .HasFilter("\"Status\" = 'Processing'");
        message.HasIndex(x => new { x.ProcessedAtUtc, x.Id })
            .HasDatabaseName("IX_outbox_messages_processed_cleanup")
            .HasFilter("\"Status\" = 'Processed'");
        message.HasIndex(x => new { x.DeadLetteredAtUtc, x.Id })
            .HasDatabaseName("IX_outbox_messages_dead_letter_list")
            .HasFilter("\"Status\" = 'DeadLetter'")
            .IsDescending()
            .HasNullSortOrder(NullSortOrder.NullsLast, NullSortOrder.NullsLast);
    }
}
