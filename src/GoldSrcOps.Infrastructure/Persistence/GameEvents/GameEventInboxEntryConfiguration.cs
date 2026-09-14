using GoldSrcOps.Domain.GameEvents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GoldSrcOps.Infrastructure.Persistence.GameEvents;

internal sealed class GameEventInboxEntryConfiguration : IEntityTypeConfiguration<GameEventInboxEntry>
{
    internal const string SourceSequenceIndex = "UX_game_event_inbox_source_sequence";

    public void Configure(EntityTypeBuilder<GameEventInboxEntry> entry)
    {
        entry.ToTable("game_event_inbox", table =>
        {
            table.HasCheckConstraint(
                "CK_game_event_inbox_ContractVersion",
                "\"ContractVersion\" > 0");
            table.HasCheckConstraint(
                "CK_game_event_inbox_SequenceNumber",
                "\"SequenceNumber\" > 0");
            table.HasCheckConstraint(
                "CK_game_event_inbox_Population",
                """
                ("Players" IS NULL AND "Bots" IS NULL)
                OR ("Players" BETWEEN 0 AND 255
                    AND "Bots" BETWEEN 0 AND "Players")
                """);
        });

        entry.HasKey(x => x.Id);
        entry.Property(x => x.Id).ValueGeneratedNever();
        entry.Property(x => x.SourceInstanceId).IsRequired();
        entry.Property(x => x.SequenceNumber).IsRequired();
        entry.Property(x => x.ContractVersion).IsRequired();
        entry.Property(x => x.Type)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();
        entry.Property(x => x.OccurredAtUtc).IsRequired();
        entry.Property(x => x.ReceivedAtUtc).IsRequired();
        entry.Property(x => x.Map).HasMaxLength(GameEventInboxEntry.MaxMapLength);
        entry.Property(x => x.IntentHash)
            .HasMaxLength(GameEventInboxEntry.MaxIntentHashLength)
            .IsRequired();

        entry.HasIndex(x => new { x.ServerId, x.SourceInstanceId, x.SequenceNumber })
            .HasDatabaseName(SourceSequenceIndex)
            .IsUnique();
        entry.HasIndex(x => new { x.ReceivedAtUtc, x.Id })
            .HasDatabaseName("IX_game_event_inbox_received_retention");
        entry.HasIndex(x => new { x.ServerId, x.OccurredAtUtc, x.Id })
            .HasDatabaseName("IX_game_event_inbox_server_occurred");
        entry.HasOne(x => x.Server)
            .WithMany()
            .HasForeignKey(x => x.ServerId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
