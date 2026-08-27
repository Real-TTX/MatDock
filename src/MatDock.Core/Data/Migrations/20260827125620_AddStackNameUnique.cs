using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatDock.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStackNameUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Stack_Name",
                table: "Stack");

            migrationBuilder.CreateIndex(
                name: "IX_Stack_Name_EnvironmentId",
                table: "Stack",
                columns: new[] { "Name", "EnvironmentId" },
                unique: true,
                filter: "\"UpdateState\" <> 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Stack_Name_EnvironmentId",
                table: "Stack");

            migrationBuilder.CreateIndex(
                name: "IX_Stack_Name",
                table: "Stack",
                column: "Name");
        }
    }
}
