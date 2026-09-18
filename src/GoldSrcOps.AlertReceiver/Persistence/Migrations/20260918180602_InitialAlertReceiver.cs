using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GoldSrcOps.AlertReceiver.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialAlertReceiver : Migration
    {
        private static readonly string[] EventIncidentOrderColumns =
            ["IncidentId", "OccurredAtUtc", "Id"];

        private static readonly string[] IncidentServerStateColumns =
            ["ServerId", "State", "OpenedAtUtc"];

        private static readonly string[] IncidentStateEventColumns =
            ["State", "LastEventAtUtc"];

        private static readonly string[] PendingOutboxColumns =
            ["Status", "NextAttemptAtUtc", "CreatedAtUtc", "Id"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "receiver");

            migrationBuilder.CreateTable(
                name: "incidents",
                schema: "receiver",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ServerName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OpenedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClosedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    OpenReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    ResolutionReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    LastEventId = table.Column<Guid>(type: "uuid", nullable: false),
                    LastEventAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_incidents", x => x.Id);
                    table.CheckConstraint("CK_receiver_incidents_ConsecutiveFailures", "\"ConsecutiveFailures\" > 0");
                    table.CheckConstraint("CK_receiver_incidents_Revision", "\"Revision\" > 0");
                    table.CheckConstraint("CK_receiver_incidents_StateFields", "(\"State\" = 'Open'\n    AND \"ClosedAtUtc\" IS NULL\n    AND \"ResolutionReason\" IS NULL)\nOR (\"State\" = 'Resolved'\n    AND \"ClosedAtUtc\" IS NOT NULL\n    AND \"ResolutionReason\" IS NOT NULL)");
                });

            migrationBuilder.CreateTable(
                name: "events",
                schema: "receiver",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IncidentId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PayloadVersion = table.Column<short>(type: "smallint", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReceivedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PayloadSha256 = table.Column<string>(type: "character(64)", nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    ReceiverMode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ProviderActionSuppressed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_events", x => x.Id);
                    table.CheckConstraint("CK_receiver_events_PayloadVersion", "\"PayloadVersion\" = 1");
                    table.CheckConstraint("CK_receiver_events_Suppression", "(\"ReceiverMode\" = 'CatchUp' AND \"ProviderActionSuppressed\")\nOR (\"ReceiverMode\" = 'Live' AND NOT \"ProviderActionSuppressed\")");
                    table.ForeignKey(
                        name: "FK_events_incidents_IncidentId",
                        column: x => x.IncidentId,
                        principalSchema: "receiver",
                        principalTable: "incidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "provider_outbox_messages",
                schema: "receiver",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceEventId = table.Column<Guid>(type: "uuid", nullable: false),
                    IncidentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_outbox_messages", x => x.Id);
                    table.CheckConstraint("CK_receiver_provider_outbox_AttemptCount", "\"AttemptCount\" >= 0");
                    table.ForeignKey(
                        name: "FK_provider_outbox_messages_events_SourceEventId",
                        column: x => x.SourceEventId,
                        principalSchema: "receiver",
                        principalTable: "events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_provider_outbox_messages_incidents_IncidentId",
                        column: x => x.IncidentId,
                        principalSchema: "receiver",
                        principalTable: "incidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_events_IncidentId_OccurredAtUtc_Id",
                schema: "receiver",
                table: "events",
                columns: EventIncidentOrderColumns);

            migrationBuilder.CreateIndex(
                name: "IX_incidents_ServerId_State_OpenedAtUtc",
                schema: "receiver",
                table: "incidents",
                columns: IncidentServerStateColumns);

            migrationBuilder.CreateIndex(
                name: "IX_incidents_State_LastEventAtUtc",
                schema: "receiver",
                table: "incidents",
                columns: IncidentStateEventColumns);

            migrationBuilder.CreateIndex(
                name: "IX_provider_outbox_messages_IncidentId",
                schema: "receiver",
                table: "provider_outbox_messages",
                column: "IncidentId");

            migrationBuilder.CreateIndex(
                name: "IX_receiver_provider_outbox_pending",
                schema: "receiver",
                table: "provider_outbox_messages",
                columns: PendingOutboxColumns,
                filter: "\"Status\" = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "UX_receiver_provider_outbox_SourceEventId",
                schema: "receiver",
                table: "provider_outbox_messages",
                column: "SourceEventId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "provider_outbox_messages",
                schema: "receiver");

            migrationBuilder.DropTable(
                name: "events",
                schema: "receiver");

            migrationBuilder.DropTable(
                name: "incidents",
                schema: "receiver");
        }
    }
}
