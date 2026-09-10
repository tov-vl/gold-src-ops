using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GoldSrcOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddServerConfigurationRevision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "Revision",
                schema: "goldsrcops",
                table: "servers",
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
                table: "servers");
        }
    }
}
