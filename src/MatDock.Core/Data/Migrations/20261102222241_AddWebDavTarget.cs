using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatDock.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWebDavTarget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EncryptedWebDavPassword",
                table: "BackupTarget",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WebDavUrl",
                table: "BackupTarget",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WebDavUsername",
                table: "BackupTarget",
                type: "TEXT",
                maxLength: 255,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EncryptedWebDavPassword",
                table: "BackupTarget");

            migrationBuilder.DropColumn(
                name: "WebDavUrl",
                table: "BackupTarget");

            migrationBuilder.DropColumn(
                name: "WebDavUsername",
                table: "BackupTarget");
        }
    }
}
