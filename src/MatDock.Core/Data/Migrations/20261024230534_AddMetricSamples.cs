using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatDock.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMetricSamples : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MetricSample",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EnvironmentId = table.Column<long>(type: "INTEGER", nullable: false),
                    Container = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    TimeUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Cpu = table.Column<double>(type: "REAL", nullable: false),
                    Mem = table.Column<double>(type: "REAL", nullable: false),
                    MemBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    Disk = table.Column<double>(type: "REAL", nullable: true),
                    Hourly = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetricSample", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MetricSample_EnvironmentId_Container_TimeUtc",
                table: "MetricSample",
                columns: new[] { "EnvironmentId", "Container", "TimeUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MetricSample_Hourly_TimeUtc",
                table: "MetricSample",
                columns: new[] { "Hourly", "TimeUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MetricSample");
        }
    }
}
