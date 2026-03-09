using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TalkingPointsSummary.Data;

#nullable disable

namespace TalkingPointsSummary.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260309163000_MessageExternalIdsPerParent")]
    public class MessageExternalIdsPerParent : Migration
    {
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