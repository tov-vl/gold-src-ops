using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861

namespace GoldSrcOps.AlertReceiver.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderDeliveryLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_provider_outbox_messages_IncidentId",
                schema: "receiver",
                table: "provider_outbox_messages");

            migrationBuilder.AddColumn<Guid>(
                name: "ClaimId",
                schema: "receiver",
                table: "provider_outbox_messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ClaimedAtUtc",
                schema: "receiver",
                table: "provider_outbox_messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeadLetteredAtUtc",
                schema: "receiver",
                table: "provider_outbox_messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastError",
                schema: "receiver",
                table: "provider_outbox_messages",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ProcessedAtUtc",
                schema: "receiver",
                table: "provider_outbox_messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_receiver_provider_outbox_incident_order",
                schema: "receiver",
                table: "provider_outbox_messages",
                columns: new[] { "IncidentId", "CreatedAtUtc", "Id" },
                filter: "\"Status\" IN ('Pending', 'Processing', 'DeadLetter')");

            migrationBuilder.CreateIndex(
                name: "IX_receiver_provider_outbox_processed",
                schema: "receiver",
                table: "provider_outbox_messages",
                columns: new[] { "ProcessedAtUtc", "Id" },
                filter: "\"Status\" = 'Processed'");

            migrationBuilder.CreateIndex(
                name: "IX_receiver_provider_outbox_processing",
                schema: "receiver",
                table: "provider_outbox_messages",
                columns: new[] { "Status", "ClaimedAtUtc" },
                filter: "\"Status\" = 'Processing'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_receiver_provider_outbox_StateFields",
                schema: "receiver",
                table: "provider_outbox_messages",
                sql: "(\"Status\" = 'Pending'\n    AND \"ClaimId\" IS NULL\n    AND \"ClaimedAtUtc\" IS NULL\n    AND \"ProcessedAtUtc\" IS NULL\n    AND \"DeadLetteredAtUtc\" IS NULL)\nOR (\"Status\" = 'Processing'\n    AND \"ClaimId\" IS NOT NULL\n    AND \"ClaimedAtUtc\" IS NOT NULL\n    AND \"ProcessedAtUtc\" IS NULL\n    AND \"DeadLetteredAtUtc\" IS NULL)\nOR (\"Status\" = 'Processed'\n    AND \"ClaimId\" IS NULL\n    AND \"ClaimedAtUtc\" IS NULL\n    AND \"ProcessedAtUtc\" IS NOT NULL\n    AND \"DeadLetteredAtUtc\" IS NULL)\nOR (\"Status\" = 'DeadLetter'\n    AND \"ClaimId\" IS NULL\n    AND \"ClaimedAtUtc\" IS NULL\n    AND \"ProcessedAtUtc\" IS NULL\n    AND \"DeadLetteredAtUtc\" IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_receiver_provider_outbox_incident_order",
                schema: "receiver",
                table: "provider_outbox_messages");

            migrationBuilder.DropIndex(
                name: "IX_receiver_provider_outbox_processed",
                schema: "receiver",
                table: "provider_outbox_messages");

            migrationBuilder.DropIndex(
                name: "IX_receiver_provider_outbox_processing",
                schema: "receiver",
                table: "provider_outbox_messages");

            migrationBuilder.DropCheckConstraint(
                name: "CK_receiver_provider_outbox_StateFields",
                schema: "receiver",
                table: "provider_outbox_messages");

            migrationBuilder.DropColumn(
                name: "ClaimId",
                schema: "receiver",
                table: "provider_outbox_messages");

            migrationBuilder.DropColumn(
                name: "ClaimedAtUtc",
                schema: "receiver",
                table: "provider_outbox_messages");

            migrationBuilder.DropColumn(
                name: "DeadLetteredAtUtc",
                schema: "receiver",
                table: "provider_outbox_messages");

            migrationBuilder.DropColumn(
                name: "LastError",
                schema: "receiver",
                table: "provider_outbox_messages");

            migrationBuilder.DropColumn(
                name: "ProcessedAtUtc",
                schema: "receiver",
                table: "provider_outbox_messages");

            migrationBuilder.CreateIndex(
                name: "IX_provider_outbox_messages_IncidentId",
                schema: "receiver",
                table: "provider_outbox_messages",
                column: "IncidentId");
        }
    }
}
