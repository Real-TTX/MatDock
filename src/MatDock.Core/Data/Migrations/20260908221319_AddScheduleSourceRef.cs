using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatDock.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduleSourceRef : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "SourceId",
                table: "ScheduledTask",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceKind",
                table: "ScheduledTask",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledTask_SourceKind_SourceId",
                table: "ScheduledTask",
                columns: new[] { "SourceKind", "SourceId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ScheduledTask_SourceKind_SourceId",
                table: "ScheduledTask");

            migrationBuilder.DropColumn(
                name: "SourceId",
                table: "ScheduledTask");

            migrationBuilder.DropColumn(
                name: "SourceKind",
                table: "ScheduledTask");
        }
    }
}
