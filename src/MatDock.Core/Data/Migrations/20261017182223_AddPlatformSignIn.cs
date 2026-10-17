using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatDock.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformSignIn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IdToken",
                table: "UserSession",
                type: "TEXT",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalSubject",
                table: "User",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LastRunOk",
                table: "ScheduledTask",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_User_ExternalSubject",
                table: "User",
                column: "ExternalSubject",
                unique: true,
                filter: "\"ExternalSubject\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_User_ExternalSubject",
                table: "User");

            migrationBuilder.DropColumn(
                name: "IdToken",
                table: "UserSession");

            migrationBuilder.DropColumn(
                name: "ExternalSubject",
                table: "User");

            migrationBuilder.DropColumn(
                name: "LastRunOk",
                table: "ScheduledTask");
        }
    }
}
