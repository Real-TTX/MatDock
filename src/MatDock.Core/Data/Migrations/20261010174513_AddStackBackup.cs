using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatDock.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStackBackup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "StackBackupId",
                table: "VolumeBackup",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ManagedBy",
                table: "ScheduledTask",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "StackBackup",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EnvironmentId = table.Column<long>(type: "INTEGER", nullable: false),
                    EnvironmentName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    StackName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ManifestFileName = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    BackupTargetId = table.Column<long>(type: "INTEGER", nullable: true),
                    BackupTargetName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    VolumeCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Consistent = table.Column<bool>(type: "INTEGER", nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Message = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    ScheduledTaskId = table.Column<long>(type: "INTEGER", nullable: true),
                    CreateDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreateUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdateUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdateState = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StackBackup", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VolumeBackup_StackBackupId",
                table: "VolumeBackup",
                column: "StackBackupId");

            migrationBuilder.CreateIndex(
                name: "IX_StackBackup_EnvironmentId_StackName",
                table: "StackBackup",
                columns: new[] { "EnvironmentId", "StackName" });

            migrationBuilder.CreateIndex(
                name: "IX_StackBackup_ScheduledTaskId",
                table: "StackBackup",
                column: "ScheduledTaskId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StackBackup");

            migrationBuilder.DropIndex(
                name: "IX_VolumeBackup_StackBackupId",
                table: "VolumeBackup");

            migrationBuilder.DropColumn(
                name: "StackBackupId",
                table: "VolumeBackup");

            migrationBuilder.DropColumn(
                name: "ManagedBy",
                table: "ScheduledTask");
        }
    }
}
