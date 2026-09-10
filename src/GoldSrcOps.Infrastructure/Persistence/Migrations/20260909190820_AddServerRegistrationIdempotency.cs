using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GoldSrcOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddServerRegistrationIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RegistrationIntentHash",
                schema: "goldsrcops",
                table: "servers",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RegistrationRequestId",
                schema: "goldsrcops",
                table: "servers",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_servers_registration_request_id",
                schema: "goldsrcops",
                table: "servers",
                column: "RegistrationRequestId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_servers_registration_request_id",
                schema: "goldsrcops",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "RegistrationIntentHash",
                schema: "goldsrcops",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "RegistrationRequestId",
                schema: "goldsrcops",
                table: "servers");
        }
    }
}
