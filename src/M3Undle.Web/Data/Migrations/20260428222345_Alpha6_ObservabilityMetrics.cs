using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M3Undle.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Alpha6_ObservabilityMetrics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "observability_metrics_enable_channel_labels",
                table: "site_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "observability_metrics_enabled",
                table: "site_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "observability_metrics_local_allowed_cidrs",
                table: "site_settings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "observability_metrics_mode",
                table: "site_settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "LocalOnly");

            migrationBuilder.CreateTable(
                name: "metrics_tokens",
                columns: table => new
                {
                    metrics_token_id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    token_hash = table.Column<string>(type: "TEXT", nullable: false),
                    scope = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "metrics:read"),
                    created_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    last_used_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    expires_utc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_metrics_tokens", x => x.metrics_token_id);
                });

            migrationBuilder.UpdateData(
                table: "site_settings",
                keyColumn: "id",
                keyValue: 1,
                columns: new[] { "observability_metrics_enabled", "observability_metrics_local_allowed_cidrs", "observability_metrics_mode" },
                values: new object[] { true, null, "LocalOnly" });

            migrationBuilder.CreateIndex(
                name: "IX_metrics_tokens_name",
                table: "metrics_tokens",
                column: "name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "metrics_tokens");

            migrationBuilder.DropColumn(
                name: "observability_metrics_enable_channel_labels",
                table: "site_settings");

            migrationBuilder.DropColumn(
                name: "observability_metrics_enabled",
                table: "site_settings");

            migrationBuilder.DropColumn(
                name: "observability_metrics_local_allowed_cidrs",
                table: "site_settings");

            migrationBuilder.DropColumn(
                name: "observability_metrics_mode",
                table: "site_settings");
        }
    }
}
