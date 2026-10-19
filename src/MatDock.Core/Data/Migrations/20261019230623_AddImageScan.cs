using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatDock.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddImageScan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ImageScan",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EnvironmentId = table.Column<long>(type: "INTEGER", nullable: false),
                    ImageRef = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    ScannedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Critical = table.Column<int>(type: "INTEGER", nullable: false),
                    High = table.Column<int>(type: "INTEGER", nullable: false),
                    Medium = table.Column<int>(type: "INTEGER", nullable: false),
                    Low = table.Column<int>(type: "INTEGER", nullable: false),
                    Unknown = table.Column<int>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    FindingsJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreateDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreateUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdateUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdateState = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImageScan", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImageScan_EnvironmentId_ImageRef",
                table: "ImageScan",
                columns: new[] { "EnvironmentId", "ImageRef" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImageScan");
        }
    }
}
