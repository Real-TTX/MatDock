using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatDock.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEnvironmentBaseUrl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BaseUrl",
                table: "Environment",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BaseUrl",
                table: "Environment");
        }
    }
}
