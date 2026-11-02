using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatDock.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUpdateAgeAndScanGate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MinAgeHours",
                table: "UpdatePolicy",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VulnerabilityGate",
                table: "UpdatePolicy",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FirstSeenUtc",
                table: "UpdateNotice",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MinAgeHours",
                table: "UpdatePolicy");

            migrationBuilder.DropColumn(
                name: "VulnerabilityGate",
                table: "UpdatePolicy");

            migrationBuilder.DropColumn(
                name: "FirstSeenUtc",
                table: "UpdateNotice");
        }
    }
}
