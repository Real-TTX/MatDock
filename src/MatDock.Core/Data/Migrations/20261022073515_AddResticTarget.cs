using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatDock.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddResticTarget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EncryptedResticEnv",
                table: "BackupTarget",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EncryptedResticPassword",
                table: "BackupTarget",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResticRepository",
                table: "BackupTarget",
                type: "TEXT",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EncryptedResticEnv",
                table: "BackupTarget");

            migrationBuilder.DropColumn(
                name: "EncryptedResticPassword",
                table: "BackupTarget");

            migrationBuilder.DropColumn(
                name: "ResticRepository",
                table: "BackupTarget");
        }
    }
}
