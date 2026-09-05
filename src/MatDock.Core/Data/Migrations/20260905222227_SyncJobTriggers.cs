using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatDock.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class SyncJobTriggers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Enabled",
                table: "SyncJob",
                newName: "WebhookEnabled");

            migrationBuilder.AddColumn<bool>(
                name: "ScheduleEnabled",
                table: "SyncJob",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ScheduleEnabled",
                table: "SyncJob");

            migrationBuilder.RenameColumn(
                name: "WebhookEnabled",
                table: "SyncJob",
                newName: "Enabled");
        }
    }
}
