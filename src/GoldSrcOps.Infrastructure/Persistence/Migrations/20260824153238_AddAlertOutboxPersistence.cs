using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GoldSrcOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAlertOutboxPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "goldsrcops",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PayloadVersion = table.Column<short>(type: "smallint", nullable: false),
                    AggregateType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AggregateId = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClaimId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClaimedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ProcessedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_messages", x => x.Id);
                    table.CheckConstraint("CK_outbox_messages_AttemptCount", "\"AttemptCount\" >= 0");
                    table.CheckConstraint("CK_outbox_messages_PayloadVersion", "\"PayloadVersion\" > 0");
                    table.CheckConstraint("CK_outbox_messages_StatusFields", "(\"Status\" = 'Pending'\n    AND \"ClaimId\" IS NULL\n    AND \"ClaimedAtUtc\" IS NULL\n    AND \"ProcessedAtUtc\" IS NULL)\nOR (\"Status\" = 'Processing'\n    AND \"ClaimId\" IS NOT NULL\n    AND \"ClaimedAtUtc\" IS NOT NULL\n    AND \"ProcessedAtUtc\" IS NULL)\nOR (\"Status\" = 'Processed'\n    AND \"ClaimId\" IS NULL\n    AND \"ClaimedAtUtc\" IS NULL\n    AND \"ProcessedAtUtc\" IS NOT NULL)\nOR (\"Status\" = 'DeadLetter'\n    AND \"ClaimId\" IS NULL\n    AND \"ClaimedAtUtc\" IS NULL\n    AND \"ProcessedAtUtc\" IS NULL)");
                });

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_active_aggregate_order",
                schema: "goldsrcops",
                table: "outbox_messages",
                columns: new[] { "AggregateType", "AggregateId", "OccurredAtUtc", "Id" },
                filter: "\"Status\" IN ('Pending', 'Processing')");

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_pending_claim",
                schema: "goldsrcops",
                table: "outbox_messages",
                columns: new[] { "Status", "NextAttemptAtUtc", "OccurredAtUtc", "Id" },
                filter: "\"Status\" = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_processed_cleanup",
                schema: "goldsrcops",
                table: "outbox_messages",
                columns: new[] { "ProcessedAtUtc", "Id" },
                filter: "\"Status\" = 'Processed'");

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_processing_recovery",
                schema: "goldsrcops",
                table: "outbox_messages",
                columns: new[] { "Status", "ClaimedAtUtc" },
                filter: "\"Status\" = 'Processing'");

            migrationBuilder.CreateIndex(
                name: "UX_outbox_messages_EventType_AggregateId",
                schema: "goldsrcops",
                table: "outbox_messages",
                columns: new[] { "EventType", "AggregateId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "goldsrcops");
        }
    }
}
