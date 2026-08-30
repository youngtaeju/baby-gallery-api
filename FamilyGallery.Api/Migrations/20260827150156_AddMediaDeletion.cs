using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyGallery.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaDeletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MediaDeletions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    OriginalRelativePath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    TrashRelativePath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    OriginalFileName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    FileSize = table.Column<long>(type: "INTEGER", nullable: false),
                    DeletedByUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    DeletedByUsername = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PurgedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaDeletions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MediaDeletions_ContentHash",
                table: "MediaDeletions",
                column: "ContentHash");

            migrationBuilder.CreateIndex(
                name: "IX_MediaDeletions_PurgedAt",
                table: "MediaDeletions",
                column: "PurgedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MediaDeletions");
        }
    }
}
