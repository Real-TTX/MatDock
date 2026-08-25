using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatDock.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBackupTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "BackupTargetId",
                table: "VolumeBackup",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BackupTargetName",
                table: "VolumeBackup",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BackupTarget",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    SmbHost = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    SmbShare = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    SmbDirectory = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    SmbUsername = table.Column<string>(type: "TEXT", nullable: true),
                    SmbDomain = table.Column<string>(type: "TEXT", nullable: true),
                    EncryptedSmbPassword = table.Column<string>(type: "TEXT", nullable: true),
                    CreateDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreateUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdateUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdateState = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupTarget", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BackupTarget");

            migrationBuilder.DropColumn(
                name: "BackupTargetId",
                table: "VolumeBackup");

            migrationBuilder.DropColumn(
                name: "BackupTargetName",
                table: "VolumeBackup");
        }
    }
}
