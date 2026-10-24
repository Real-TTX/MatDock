using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatDock.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAlerting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "HostCpuLimit",
                table: "NotificationSettings",
                type: "INTEGER",
                nullable: true);

            // The settings that exist get the defaults of a new installation: the disk at 90 %, three starts in ten minutes.
            migrationBuilder.AddColumn<int>(
                name: "HostDiskLimit",
                table: "NotificationSettings",
                type: "INTEGER",
                nullable: true,
                defaultValue: 90);

            migrationBuilder.AddColumn<int>(
                name: "HostMemLimit",
                table: "NotificationSettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RestartLoopCount",
                table: "NotificationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<int>(
                name: "RestartLoopMinutes",
                table: "NotificationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.AddColumn<int>(
                name: "AlertCpuPercent",
                table: "Environment",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AlertDiskPercent",
                table: "Environment",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AlertMemPercent",
                table: "Environment",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "NotificationChannel",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Target = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    EncryptedSecret = table.Column<string>(type: "TEXT", nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreateUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdateUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdateState = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationChannel", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationChannel_Name",
                table: "NotificationChannel",
                column: "Name");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationChannel");

            migrationBuilder.DropColumn(
                name: "HostCpuLimit",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "HostDiskLimit",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "HostMemLimit",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "RestartLoopCount",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "RestartLoopMinutes",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "AlertCpuPercent",
                table: "Environment");

            migrationBuilder.DropColumn(
                name: "AlertDiskPercent",
                table: "Environment");

            migrationBuilder.DropColumn(
                name: "AlertMemPercent",
                table: "Environment");
        }
    }
}
