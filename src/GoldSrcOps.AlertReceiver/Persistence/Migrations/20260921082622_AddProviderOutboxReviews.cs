using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GoldSrcOps.AlertReceiver.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderOutboxReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "provider_outbox_reviews",
                schema: "receiver",
                columns: table => new
                {
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ReviewedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_outbox_reviews", x => x.RequestId);
                    table.ForeignKey(
                        name: "FK_provider_outbox_reviews_provider_outbox_messages_MessageId",
                        column: x => x.MessageId,
                        principalSchema: "receiver",
                        principalTable: "provider_outbox_messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UX_receiver_provider_outbox_reviews_MessageId",
                schema: "receiver",
                table: "provider_outbox_reviews",
                column: "MessageId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "provider_outbox_reviews",
                schema: "receiver");
        }
    }
}
