using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatDock.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStackGitSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GitComposePath",
                table: "Stack",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "GitCredentialId",
                table: "Stack",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GitReference",
                table: "Stack",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GitRepoUrl",
                table: "Stack",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GitComposePath",
                table: "Stack");

            migrationBuilder.DropColumn(
                name: "GitCredentialId",
                table: "Stack");

            migrationBuilder.DropColumn(
                name: "GitReference",
                table: "Stack");

            migrationBuilder.DropColumn(
                name: "GitRepoUrl",
                table: "Stack");
        }
    }
}
