using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GoldSrcOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboxReplayPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeadLetteredAtUtc",
                schema: "goldsrcops",
                table: "outbox_messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReplayCount",
                schema: "goldsrcops",
                table: "outbox_messages",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "outbox_replay_requests",
                schema: "goldsrcops",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OutboxMessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PayloadVersion = table.Column<short>(type: "smallint", nullable: false),
                    AggregateType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AggregateId = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RequestedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RequestedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ReplayNumber = table.Column<int>(type: "integer", nullable: false),
                    PreviousAttemptCount = table.Column<int>(type: "integer", nullable: false),
                    PreviousDeadLetteredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PreviousLastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_replay_requests", x => x.Id);
                    table.CheckConstraint("CK_outbox_replay_requests_PayloadVersion", "\"PayloadVersion\" > 0");
                    table.CheckConstraint("CK_outbox_replay_requests_PreviousAttemptCount", "\"PreviousAttemptCount\" >= 0");
                    table.CheckConstraint("CK_outbox_replay_requests_ReplayNumber", "\"ReplayNumber\" > 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_dead_letter_list",
                schema: "goldsrcops",
                table: "outbox_messages",
                columns: new[] { "DeadLetteredAtUtc", "Id" },
                descending: new bool[0],
                filter: "\"Status\" = 'DeadLetter'")
                .Annotation("Npgsql:IndexNullSortOrder", new[] { NullSortOrder.NullsLast, NullSortOrder.NullsLast });

            migrationBuilder.AddCheckConstraint(
                name: "CK_outbox_messages_DeadLetteredAtUtc",
                schema: "goldsrcops",
                table: "outbox_messages",
                sql: "\"DeadLetteredAtUtc\" IS NULL OR \"Status\" = 'DeadLetter'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_outbox_messages_ReplayCount",
                schema: "goldsrcops",
                table: "outbox_messages",
                sql: "\"ReplayCount\" >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_outbox_replay_requests_Message_RequestedAtUtc",
                schema: "goldsrcops",
                table: "outbox_replay_requests",
                columns: new[] { "OutboxMessageId", "RequestedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "UX_outbox_replay_requests_Message_ReplayNumber",
                schema: "goldsrcops",
                table: "outbox_replay_requests",
                columns: new[] { "OutboxMessageId", "ReplayNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbox_replay_requests",
                schema: "goldsrcops");

            migrationBuilder.DropIndex(
                name: "IX_outbox_messages_dead_letter_list",
                schema: "goldsrcops",
                table: "outbox_messages");

            migrationBuilder.DropCheckConstraint(
                name: "CK_outbox_messages_DeadLetteredAtUtc",
                schema: "goldsrcops",
                table: "outbox_messages");

            migrationBuilder.DropCheckConstraint(
                name: "CK_outbox_messages_ReplayCount",
                schema: "goldsrcops",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "DeadLetteredAtUtc",
                schema: "goldsrcops",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "ReplayCount",
                schema: "goldsrcops",
                table: "outbox_messages");
        }
    }
}
