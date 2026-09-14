using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GoldSrcOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGameEventInboxFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "game_event_inbox",
                schema: "goldsrcops",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SequenceNumber = table.Column<long>(type: "bigint", nullable: false),
                    ContractVersion = table.Column<short>(type: "smallint", nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReceivedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Map = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Players = table.Column<int>(type: "integer", nullable: true),
                    Bots = table.Column<int>(type: "integer", nullable: true),
                    IntentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_game_event_inbox", x => x.Id);
                    table.CheckConstraint("CK_game_event_inbox_ContractVersion", "\"ContractVersion\" > 0");
                    table.CheckConstraint("CK_game_event_inbox_Population", "(\"Players\" IS NULL AND \"Bots\" IS NULL)\nOR (\"Players\" BETWEEN 0 AND 255\n    AND \"Bots\" BETWEEN 0 AND \"Players\")");
                    table.CheckConstraint("CK_game_event_inbox_SequenceNumber", "\"SequenceNumber\" > 0");
                    table.ForeignKey(
                        name: "FK_game_event_inbox_servers_ServerId",
                        column: x => x.ServerId,
                        principalSchema: "goldsrcops",
                        principalTable: "servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_game_event_inbox_received_retention",
                schema: "goldsrcops",
                table: "game_event_inbox",
                columns: new[] { "ReceivedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_game_event_inbox_server_occurred",
                schema: "goldsrcops",
                table: "game_event_inbox",
                columns: new[] { "ServerId", "OccurredAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "UX_game_event_inbox_source_sequence",
                schema: "goldsrcops",
                table: "game_event_inbox",
                columns: new[] { "ServerId", "SourceInstanceId", "SequenceNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "game_event_inbox",
                schema: "goldsrcops");
        }
    }
}
