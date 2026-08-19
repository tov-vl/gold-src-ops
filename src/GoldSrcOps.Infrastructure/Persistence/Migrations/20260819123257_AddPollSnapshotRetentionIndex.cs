using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GoldSrcOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPollSnapshotRetentionIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_poll_snapshots_CheckedAtUtc_Id",
                schema: "goldsrcops",
                table: "poll_snapshots",
                columns: new[] { "CheckedAtUtc", "Id" })
                .Annotation("Npgsql:CreatedConcurrently", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_poll_snapshots_CheckedAtUtc_Id",
                schema: "goldsrcops",
                table: "poll_snapshots");
        }
    }
}
