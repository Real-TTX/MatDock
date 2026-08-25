using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatDock.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVolumeBackup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VolumeBackup",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceEnvironmentId = table.Column<long>(type: "INTEGER", nullable: true),
                    SourceEnvironmentName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    VolumeName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    CreateDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreateUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdateUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdateState = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VolumeBackup", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VolumeBackup_VolumeName",
                table: "VolumeBackup",
                column: "VolumeName");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VolumeBackup");
        }
    }
}
