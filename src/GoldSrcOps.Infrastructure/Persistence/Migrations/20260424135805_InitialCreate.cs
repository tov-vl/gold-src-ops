using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GoldSrcOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "goldsrcops");

            migrationBuilder.CreateTable(
                name: "servers",
                schema: "goldsrcops",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Game = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    host = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    query_port = table.Column<int>(type: "integer", nullable: false),
                    rcon_port = table.Column<int>(type: "integer", nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    PollIntervalSeconds = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_servers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "availability_incidents",
                schema: "goldsrcops",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OpenedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClosedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StartReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    EndReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_availability_incidents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_availability_incidents_servers_ServerId",
                        column: x => x.ServerId,
                        principalSchema: "goldsrcops",
                        principalTable: "servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "poll_snapshots",
                schema: "goldsrcops",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    CheckedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsReachable = table.Column<bool>(type: "boolean", nullable: false),
                    LatencyMs = table.Column<int>(type: "integer", nullable: true),
                    Map = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Players = table.Column<int>(type: "integer", nullable: true),
                    MaxPlayers = table.Column<int>(type: "integer", nullable: true),
                    Bots = table.Column<int>(type: "integer", nullable: true),
                    RawVersion = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_poll_snapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_poll_snapshots_servers_ServerId",
                        column: x => x.ServerId,
                        principalSchema: "goldsrcops",
                        principalTable: "servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "server_current_states",
                schema: "goldsrcops",
                columns: table => new
                {
                    ServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IsReachable = table.Column<bool>(type: "boolean", nullable: false),
                    LastCheckedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSuccessAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LatencyMs = table.Column<int>(type: "integer", nullable: true),
                    CurrentMap = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Players = table.Column<int>(type: "integer", nullable: true),
                    MaxPlayers = table.Column<int>(type: "integer", nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_server_current_states", x => x.ServerId);
                    table.ForeignKey(
                        name: "FK_server_current_states_servers_ServerId",
                        column: x => x.ServerId,
                        principalSchema: "goldsrcops",
                        principalTable: "servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_availability_incidents_ServerId_ClosedAtUtc",
                schema: "goldsrcops",
                table: "availability_incidents",
                columns: new[] { "ServerId", "ClosedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_poll_snapshots_ServerId_CheckedAtUtc",
                schema: "goldsrcops",
                table: "poll_snapshots",
                columns: new[] { "ServerId", "CheckedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "availability_incidents",
                schema: "goldsrcops");

            migrationBuilder.DropTable(
                name: "poll_snapshots",
                schema: "goldsrcops");

            migrationBuilder.DropTable(
                name: "server_current_states",
                schema: "goldsrcops");

            migrationBuilder.DropTable(
                name: "servers",
                schema: "goldsrcops");
        }
    }
}
