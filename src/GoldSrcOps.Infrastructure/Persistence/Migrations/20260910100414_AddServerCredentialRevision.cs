using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GoldSrcOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddServerCredentialRevision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameIndex(
                name: "IX_server_credentials_ServerId_Kind",
                schema: "goldsrcops",
                table: "server_credentials",
                newName: "ux_server_credentials_server_id_kind");

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                schema: "goldsrcops",
                table: "server_credentials",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Revision",
                schema: "goldsrcops",
                table: "server_credentials");

            migrationBuilder.RenameIndex(
                name: "ux_server_credentials_server_id_kind",
                schema: "goldsrcops",
                table: "server_credentials",
                newName: "IX_server_credentials_ServerId_Kind");
        }
    }
}
