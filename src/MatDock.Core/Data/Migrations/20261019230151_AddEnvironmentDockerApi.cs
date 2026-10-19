using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatDock.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEnvironmentDockerApi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EncryptedTlsKey",
                table: "Environment",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TlsCa",
                table: "Environment",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TlsCert",
                table: "Environment",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EncryptedTlsKey",
                table: "Environment");

            migrationBuilder.DropColumn(
                name: "TlsCa",
                table: "Environment");

            migrationBuilder.DropColumn(
                name: "TlsCert",
                table: "Environment");
        }
    }
}
