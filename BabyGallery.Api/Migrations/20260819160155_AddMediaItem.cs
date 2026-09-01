using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BabyGallery.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaItem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MediaItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RelativePath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    OriginalFileName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    MediaType = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    FileSize = table.Column<long>(type: "INTEGER", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CapturedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FileModifiedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IndexedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Width = table.Column<int>(type: "INTEGER", nullable: true),
                    Height = table.Column<int>(type: "INTEGER", nullable: true),
                    DurationMs = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaItems", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_CapturedAt_Id",
                table: "MediaItems",
                columns: new[] { "CapturedAt", "Id" },
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_ContentHash",
                table: "MediaItems",
                column: "ContentHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_RelativePath",
                table: "MediaItems",
                column: "RelativePath",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MediaItems");
        }
    }
}
