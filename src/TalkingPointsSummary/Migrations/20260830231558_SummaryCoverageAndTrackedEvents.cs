using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TalkingPointsSummary.Migrations
{
    /// <inheritdoc />
    public partial class SummaryCoverageAndTrackedEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CritiqueLog",
                table: "Summaries",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EmailSentAt",
                table: "Summaries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RevisionCount",
                table: "Summaries",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "IncludedInSummaryId",
                table: "NewsItems",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TrackedEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ParentId = table.Column<int>(type: "integer", nullable: false),
                    SourceNewsItemId = table.Column<int>(type: "integer", nullable: false),
                    School = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EventDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TimeText = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SupersededByEventId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackedEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TrackedEvents_NewsItems_SourceNewsItemId",
                        column: x => x.SourceNewsItemId,
                        principalTable: "NewsItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TrackedEvents_Parents_ParentId",
                        column: x => x.ParentId,
                        principalTable: "Parents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TrackedEvents_TrackedEvents_SupersededByEventId",
                        column: x => x.SupersededByEventId,
                        principalTable: "TrackedEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NewsItems_IncludedInSummaryId",
                table: "NewsItems",
                column: "IncludedInSummaryId");

            migrationBuilder.CreateIndex(
                name: "IX_NewsItems_ParentId_IncludedInSummaryId",
                table: "NewsItems",
                columns: new[] { "ParentId", "IncludedInSummaryId" });

            migrationBuilder.CreateIndex(
                name: "IX_TrackedEvents_ParentId_School_EventDate_Title",
                table: "TrackedEvents",
                columns: new[] { "ParentId", "School", "EventDate", "Title" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TrackedEvents_ParentId_Status_EventDate",
                table: "TrackedEvents",
                columns: new[] { "ParentId", "Status", "EventDate" });

            migrationBuilder.CreateIndex(
                name: "IX_TrackedEvents_SourceNewsItemId",
                table: "TrackedEvents",
                column: "SourceNewsItemId");

            migrationBuilder.CreateIndex(
                name: "IX_TrackedEvents_SupersededByEventId",
                table: "TrackedEvents",
                column: "SupersededByEventId");

            migrationBuilder.AddForeignKey(
                name: "FK_NewsItems_Summaries_IncludedInSummaryId",
                table: "NewsItems",
                column: "IncludedInSummaryId",
                principalTable: "Summaries",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_NewsItems_Summaries_IncludedInSummaryId",
                table: "NewsItems");

            migrationBuilder.DropTable(
                name: "TrackedEvents");

            migrationBuilder.DropIndex(
                name: "IX_NewsItems_IncludedInSummaryId",
                table: "NewsItems");

            migrationBuilder.DropIndex(
                name: "IX_NewsItems_ParentId_IncludedInSummaryId",
                table: "NewsItems");

            migrationBuilder.DropColumn(
                name: "CritiqueLog",
                table: "Summaries");

            migrationBuilder.DropColumn(
                name: "EmailSentAt",
                table: "Summaries");

            migrationBuilder.DropColumn(
                name: "RevisionCount",
                table: "Summaries");

            migrationBuilder.DropColumn(
                name: "IncludedInSummaryId",
                table: "NewsItems");
        }
    }
}
