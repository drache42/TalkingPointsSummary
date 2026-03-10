using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TalkingPointsSummary.Migrations
{
    /// <inheritdoc />
    public partial class NewsItemSourceTypeUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_NewsItems_ParentId_SourceMessageId_SourceType",
                table: "NewsItems",
                columns: new[] { "ParentId", "SourceMessageId", "SourceType" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_NewsItems_ParentId_SourceMessageId_SourceType",
                table: "NewsItems");
        }
    }
}
