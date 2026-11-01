using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatDock.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStackDeployments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeployOptionsJson",
                table: "Stack",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "StackDeployment",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StackId = table.Column<long>(type: "INTEGER", nullable: false),
                    StackName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    EnvironmentId = table.Column<long>(type: "INTEGER", nullable: false),
                    Trigger = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Actor = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Options = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CommitSha = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Ok = table.Column<bool>(type: "INTEGER", nullable: true),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Log = table.Column<string>(type: "TEXT", nullable: true),
                    CreateDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreateUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdateUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdateState = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StackDeployment", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StackDeployment_StackId_StartedAt",
                table: "StackDeployment",
                columns: new[] { "StackId", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StackDeployment");

            migrationBuilder.DropColumn(
                name: "DeployOptionsJson",
                table: "Stack");
        }
    }
}
