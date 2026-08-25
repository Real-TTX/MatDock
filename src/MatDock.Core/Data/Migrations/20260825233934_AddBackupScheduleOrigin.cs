using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatDock.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBackupScheduleOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "BackupScheduleId",
                table: "VolumeBackup",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BackupScheduleId",
                table: "VolumeBackup");
        }
    }
}
