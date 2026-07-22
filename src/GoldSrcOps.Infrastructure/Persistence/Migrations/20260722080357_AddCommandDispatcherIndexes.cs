using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GoldSrcOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCommandDispatcherIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_command_executions_ServerId_RequestedAtUtc",
                schema: "goldsrcops",
                table: "command_executions");

            migrationBuilder.CreateIndex(
                name: "IX_command_executions_ServerId_Status_RequestedAtUtc",
                schema: "goldsrcops",
                table: "command_executions",
                columns: new[] { "ServerId", "Status", "RequestedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_command_executions_Status_StartedAtUtc",
                schema: "goldsrcops",
                table: "command_executions",
                columns: new[] { "Status", "StartedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_command_executions_ServerId_Status_RequestedAtUtc",
                schema: "goldsrcops",
                table: "command_executions");

            migrationBuilder.DropIndex(
                name: "IX_command_executions_Status_StartedAtUtc",
                schema: "goldsrcops",
                table: "command_executions");

            migrationBuilder.CreateIndex(
                name: "IX_command_executions_ServerId_RequestedAtUtc",
                schema: "goldsrcops",
                table: "command_executions",
                columns: new[] { "ServerId", "RequestedAtUtc" });
        }
    }
}
