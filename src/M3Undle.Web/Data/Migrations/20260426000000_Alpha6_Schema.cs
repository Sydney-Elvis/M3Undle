using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M3Undle.Web.Data.Migrations
{
    public partial class Alpha6_Schema : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // XtreamDetection
            migrationBuilder.AddColumn<bool>(
                name: "xtream_detected_capable",
                table: "providers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // ProviderExpiry
            migrationBuilder.AddColumn<DateTime>(
                name: "playlist_expires_utc",
                table: "providers",
                type: "TEXT",
                nullable: true);

            // RemoveConfigTracking
            migrationBuilder.DropColumn(
                name: "config_source_path",
                table: "providers");

            migrationBuilder.DropColumn(
                name: "needs_env_var_substitution",
                table: "providers");

            // SystemEvents
            migrationBuilder.AddColumn<int>(
                name: "event_retention_days",
                table: "site_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 7);

            migrationBuilder.CreateTable(
                name: "system_events",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    event_type = table.Column<string>(type: "TEXT", nullable: false),
                    severity = table.Column<string>(type: "TEXT", nullable: false),
                    title = table.Column<string>(type: "TEXT", nullable: false),
                    detail = table.Column<string>(type: "TEXT", nullable: true),
                    provider_id = table.Column<string>(type: "TEXT", nullable: true),
                    integration_id = table.Column<string>(type: "TEXT", nullable: true),
                    occurred_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    occurrence_count = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_system_events", x => x.id);
                });

            migrationBuilder.UpdateData(
                table: "site_settings",
                keyColumn: "id",
                keyValue: 1,
                column: "event_retention_days",
                value: 7);

            migrationBuilder.CreateIndex(
                name: "ix_system_events_occurred_at",
                table: "system_events",
                column: "occurred_at");

            // CleanRelayMode
            migrationBuilder.AddColumn<string>(
                name: "clean_relay_mode",
                table: "providers",
                type: "TEXT",
                nullable: false,
                defaultValue: "off");

            // ObservabilityMetrics
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

            // PerProfileRefreshSchedule
            migrationBuilder.AddColumn<string>(
                name: "refresh_schedule_kind_override",
                table: "profiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "refresh_startup_catchup_override",
                table: "profiles",
                type: "INTEGER",
                nullable: true);

            // SystemEventIndexes
            migrationBuilder.CreateIndex(
                name: "ix_system_events_event_type_integration_id",
                table: "system_events",
                columns: new[] { "event_type", "integration_id" },
                filter: "\"integration_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_system_events_event_type_provider_id",
                table: "system_events",
                columns: new[] { "event_type", "provider_id" },
                filter: "\"provider_id\" IS NOT NULL");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // SystemEventIndexes (reverse)
            migrationBuilder.DropIndex(
                name: "ix_system_events_event_type_integration_id",
                table: "system_events");

            migrationBuilder.DropIndex(
                name: "ix_system_events_event_type_provider_id",
                table: "system_events");

            // PerProfileRefreshSchedule (reverse)
            migrationBuilder.DropColumn(
                name: "refresh_schedule_kind_override",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "refresh_startup_catchup_override",
                table: "profiles");

            // ObservabilityMetrics (reverse)
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

            // CleanRelayMode (reverse)
            migrationBuilder.DropColumn(
                name: "clean_relay_mode",
                table: "providers");

            // SystemEvents (reverse)
            migrationBuilder.DropTable(
                name: "system_events");

            migrationBuilder.DropColumn(
                name: "event_retention_days",
                table: "site_settings");

            // RemoveConfigTracking (reverse)
            migrationBuilder.AddColumn<string>(
                name: "config_source_path",
                table: "providers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "needs_env_var_substitution",
                table: "providers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // ProviderExpiry (reverse)
            migrationBuilder.DropColumn(
                name: "playlist_expires_utc",
                table: "providers");

            // XtreamDetection (reverse)
            migrationBuilder.DropColumn(
                name: "xtream_detected_capable",
                table: "providers");
        }
    }
}
