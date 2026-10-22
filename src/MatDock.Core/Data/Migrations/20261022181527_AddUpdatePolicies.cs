using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatDock.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUpdatePolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UpdateNotice",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StackId = table.Column<long>(type: "INTEGER", nullable: false),
                    Service = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Target = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Failed = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotifiedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreateDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreateUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdateUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdateState = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UpdateNotice", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UpdatePolicy",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Scope = table.Column<int>(type: "INTEGER", nullable: false),
                    ScopeId = table.Column<long>(type: "INTEGER", nullable: false),
                    Mode = table.Column<int>(type: "INTEGER", nullable: true),
                    Strategy = table.Column<int>(type: "INTEGER", nullable: true),
                    WindowStartHour = table.Column<int>(type: "INTEGER", nullable: true),
                    WindowEndHour = table.Column<int>(type: "INTEGER", nullable: true),
                    CheckEveryHours = table.Column<int>(type: "INTEGER", nullable: true),
                    PreBackup = table.Column<bool>(type: "INTEGER", nullable: true),
                    Rollback = table.Column<bool>(type: "INTEGER", nullable: true),
                    NotifyAvailable = table.Column<bool>(type: "INTEGER", nullable: true),
                    NotifyResult = table.Column<bool>(type: "INTEGER", nullable: true),
                    ServiceStrategiesJson = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    CreateDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreateUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdateUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdateState = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UpdatePolicy", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UpdateNotice_StackId_Service_Target",
                table: "UpdateNotice",
                columns: new[] { "StackId", "Service", "Target" },
                unique: true,
                filter: "\"UpdateState\" <> 0");

            migrationBuilder.CreateIndex(
                name: "IX_UpdatePolicy_Scope_ScopeId",
                table: "UpdatePolicy",
                columns: new[] { "Scope", "ScopeId" },
                unique: true,
                filter: "\"UpdateState\" <> 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UpdateNotice");

            migrationBuilder.DropTable(
                name: "UpdatePolicy");
        }
    }
}
