using GoldSrcOps.AlertReceiver.AvailabilityEvents;
using GoldSrcOps.Application.Alerts;
using Microsoft.EntityFrameworkCore;

namespace GoldSrcOps.AlertReceiver.Persistence;

public sealed class AlertReceiverDbContext(DbContextOptions<AlertReceiverDbContext> options)
    : DbContext(options)
{
    public const string Schema = "receiver";
    public const string MigrationsHistoryTable = "__EFMigrationsHistory";

    internal DbSet<ReceivedAvailabilityEvent> ReceivedEvents =>
        Set<ReceivedAvailabilityEvent>();

    internal DbSet<ReceiverIncident> Incidents => Set<ReceiverIncident>();

    internal DbSet<ProviderOutboxMessage> ProviderOutboxMessages =>
        Set<ProviderOutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<ReceiverIncident>(incident =>
        {
            incident.ToTable("incidents", table =>
            {
                table.HasCheckConstraint(
                    "CK_receiver_incidents_ConsecutiveFailures",
                    "\"ConsecutiveFailures\" > 0");
                table.HasCheckConstraint(
                    "CK_receiver_incidents_Revision",
                    "\"Revision\" > 0");
                table.HasCheckConstraint(
                    "CK_receiver_incidents_StateFields",
                    """
                    ("State" = 'Open'
                        AND "ClosedAtUtc" IS NULL
                        AND "ResolutionReason" IS NULL)
                    OR ("State" = 'Resolved'
                        AND "ClosedAtUtc" IS NOT NULL
                        AND "ResolutionReason" IS NOT NULL)
                    """);
            });
            incident.HasKey(x => x.Id);
            incident.Property(x => x.Id).ValueGeneratedNever();
            incident.Property(x => x.ServerName)
                .HasMaxLength(AvailabilityEventRequestValidator.MaxServerNameLength)
                .IsRequired();
            incident.Property(x => x.State)
                .HasConversion<string>()
                .HasMaxLength(32)
                .IsRequired();
            incident.Property(x => x.OpenReason)
                .HasMaxLength(AvailabilityEventRequestValidator.MaxReasonLength)
                .IsRequired();
            incident.Property(x => x.ResolutionReason)
                .HasMaxLength(AvailabilityEventRequestValidator.MaxReasonLength);
            incident.Property(x => x.Revision)
                .IsConcurrencyToken()
                .IsRequired();
            incident.HasIndex(x => new { x.ServerId, x.State, x.OpenedAtUtc });
            incident.HasIndex(x => new { x.State, x.LastEventAtUtc });
        });

        modelBuilder.Entity<ReceivedAvailabilityEvent>(receivedEvent =>
        {
            receivedEvent.ToTable("events", table =>
            {
                table.HasCheckConstraint(
                    "CK_receiver_events_PayloadVersion",
                    $"\"PayloadVersion\" = {IncidentAlertEventV1.CurrentPayloadVersion}");
                table.HasCheckConstraint(
                    "CK_receiver_events_Suppression",
                    """
                    ("ReceiverMode" = 'CatchUp' AND "ProviderActionSuppressed")
                    OR ("ReceiverMode" = 'Live' AND NOT "ProviderActionSuppressed")
                    """);
            });
            receivedEvent.HasKey(x => x.Id);
            receivedEvent.Property(x => x.Id).ValueGeneratedNever();
            receivedEvent.Property(x => x.EventType)
                .HasMaxLength(128)
                .IsRequired();
            receivedEvent.Property(x => x.PayloadSha256)
                .HasColumnType("character(64)")
                .IsRequired();
            receivedEvent.Property(x => x.Payload)
                .HasColumnType("jsonb")
                .IsRequired();
            receivedEvent.Property(x => x.ReceiverMode)
                .HasConversion<string>()
                .HasMaxLength(32)
                .IsRequired();
            receivedEvent.HasIndex(x => new { x.IncidentId, x.OccurredAtUtc, x.Id });
            receivedEvent.HasOne<ReceiverIncident>()
                .WithMany()
                .HasForeignKey(x => x.IncidentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ProviderOutboxMessage>(message =>
        {
            message.ToTable("provider_outbox_messages", table =>
            {
                table.HasCheckConstraint(
                    "CK_receiver_provider_outbox_AttemptCount",
                    "\"AttemptCount\" >= 0");
            });
            message.HasKey(x => x.Id);
            message.Property(x => x.Id).ValueGeneratedNever();
            message.Property(x => x.Action)
                .HasConversion<string>()
                .HasMaxLength(32)
                .IsRequired();
            message.Property(x => x.Payload)
                .HasColumnType("jsonb")
                .IsRequired();
            message.Property(x => x.Status)
                .HasConversion<string>()
                .HasMaxLength(32)
                .IsRequired();
            message.HasIndex(x => x.SourceEventId)
                .IsUnique()
                .HasDatabaseName("UX_receiver_provider_outbox_SourceEventId");
            message.HasIndex(x => new
            {
                x.Status,
                x.NextAttemptAtUtc,
                x.CreatedAtUtc,
                x.Id,
            })
                .HasDatabaseName("IX_receiver_provider_outbox_pending")
                .HasFilter("\"Status\" = 'Pending'");
            message.HasOne<ReceivedAvailabilityEvent>()
                .WithOne()
                .HasForeignKey<ProviderOutboxMessage>(x => x.SourceEventId)
                .OnDelete(DeleteBehavior.Restrict);
            message.HasOne<ReceiverIncident>()
                .WithMany()
                .HasForeignKey(x => x.IncidentId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
