using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TalkingPointsSummary.Data;

#nullable disable

namespace TalkingPointsSummary.Migrations
{
    /// <summary>
    /// Updates message indexing so external message identifiers are unique per parent.
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20260309163000_MessageExternalIdsPerParent")]
    public class MessageExternalIdsPerParent : Migration
    {
        /// <summary>
        /// Applies the migration changes.
        /// </summary>
        /// <param name="migrationBuilder">Migration builder used to define schema updates.</param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Messages_ExternalMessageId",
                table: "Messages");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ParentId_ExternalMessageId",
                table: "Messages",
                columns: new[] { "ParentId", "ExternalMessageId" },
                unique: true);
        }

        /// <summary>
        /// Reverts the migration changes.
        /// </summary>
        /// <param name="migrationBuilder">Migration builder used to define schema updates.</param>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Messages_ParentId_ExternalMessageId",
                table: "Messages");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ExternalMessageId",
                table: "Messages",
                column: "ExternalMessageId",
                unique: true);
        }
    }
}