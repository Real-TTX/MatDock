using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatDock.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "SyncJobId",
                table: "Stack",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SyncJob",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    GitRepoUrl = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    GitReference = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    GitCredentialId = table.Column<long>(type: "INTEGER", nullable: true),
                    Cron = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdateMode = table.Column<int>(type: "INTEGER", nullable: false),
                    PullImages = table.Column<bool>(type: "INTEGER", nullable: false),
                    PruneRemoved = table.Column<bool>(type: "INTEGER", nullable: false),
                    WebhookToken = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    LastCommitSha = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    NextRunAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastRunAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastStatus = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    CreateDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreateUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdateUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdateState = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncJob", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyncJobItem",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SyncJobId = table.Column<long>(type: "INTEGER", nullable: false),
                    ComposePath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    EnvironmentId = table.Column<long>(type: "INTEGER", nullable: false),
                    StackName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    StackId = table.Column<long>(type: "INTEGER", nullable: true),
                    LastDeployedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastStatus = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    CreateDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreateUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdateUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdateState = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncJobItem", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Stack_SyncJobId",
                table: "Stack",
                column: "SyncJobId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncJob_WebhookToken",
                table: "SyncJob",
                column: "WebhookToken",
                unique: true,
                filter: "\"UpdateState\" <> 0");

            migrationBuilder.CreateIndex(
                name: "IX_SyncJobItem_SyncJobId",
                table: "SyncJobItem",
                column: "SyncJobId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SyncJob");

            migrationBuilder.DropTable(
                name: "SyncJobItem");

            migrationBuilder.DropIndex(
                name: "IX_Stack_SyncJobId",
                table: "Stack");

            migrationBuilder.DropColumn(
                name: "SyncJobId",
                table: "Stack");
        }
    }
}
