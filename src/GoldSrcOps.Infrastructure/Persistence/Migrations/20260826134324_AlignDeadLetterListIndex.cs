using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GoldSrcOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AlignDeadLetterListIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_outbox_messages_dead_letter_list",
                schema: "goldsrcops",
                table: "outbox_messages");

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_dead_letter_list",
                schema: "goldsrcops",
                table: "outbox_messages",
                columns: new[] { "DeadLetteredAtUtc", "OccurredAtUtc", "Id" },
                descending: new bool[0],
                filter: "\"Status\" = 'DeadLetter'")
                .Annotation("Npgsql:IndexNullSortOrder", new[] { NullSortOrder.NullsLast, NullSortOrder.NullsLast, NullSortOrder.NullsLast });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_outbox_messages_dead_letter_list",
                schema: "goldsrcops",
                table: "outbox_messages");

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_dead_letter_list",
                schema: "goldsrcops",
                table: "outbox_messages",
                columns: new[] { "DeadLetteredAtUtc", "Id" },
                descending: new bool[0],
                filter: "\"Status\" = 'DeadLetter'")
                .Annotation("Npgsql:IndexNullSortOrder", new[] { NullSortOrder.NullsLast, NullSortOrder.NullsLast });
        }
    }
}
