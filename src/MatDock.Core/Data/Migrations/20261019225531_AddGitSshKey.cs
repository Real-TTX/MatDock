using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatDock.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGitSshKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EncryptedPassphrase",
                table: "GitCredential",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EncryptedPrivateKey",
                table: "GitCredential",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HostKeyFingerprint",
                table: "GitCredential",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EncryptedPassphrase",
                table: "GitCredential");

            migrationBuilder.DropColumn(
                name: "EncryptedPrivateKey",
                table: "GitCredential");

            migrationBuilder.DropColumn(
                name: "HostKeyFingerprint",
                table: "GitCredential");
        }
    }
}
