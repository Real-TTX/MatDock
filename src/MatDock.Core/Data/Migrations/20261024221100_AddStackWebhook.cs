using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatDock.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStackWebhook : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WebhookToken",
                table: "Stack",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Stack_WebhookToken",
                table: "Stack",
                column: "WebhookToken");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Stack_WebhookToken",
                table: "Stack");

            migrationBuilder.DropColumn(
                name: "WebhookToken",
                table: "Stack");
        }
    }
}
