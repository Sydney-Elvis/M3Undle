using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M3Undle.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Alpha4_Schema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // From Alpha4_StreamingSettings
            migrationBuilder.AddColumn<bool>(
                name: "streaming_enabled",
                table: "site_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.UpdateData(
                table: "site_settings",
                keyColumn: "id",
                keyValue: 1,
                column: "streaming_enabled",
                value: true);

            // From Alpha4_ProviderStreamLimits
            migrationBuilder.AddColumn<int>(
                name: "max_concurrent_streams",
                table: "providers",
                type: "INTEGER",
                nullable: true);

            // From Alpha4_EpgSources
            migrationBuilder.CreateTable(
                name: "epg_sources",
                columns: table => new
                {
                    epg_source_id = table.Column<string>(type: "TEXT", nullable: false),
                    provider_id = table.Column<string>(type: "TEXT", nullable: true),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "xmltv_url"),
                    url_or_path = table.Column<string>(type: "TEXT", nullable: true),
                    priority = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 10),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    headers_json = table.Column<string>(type: "TEXT", nullable: true),
                    user_agent = table.Column<string>(type: "TEXT", nullable: true),
                    timeout_seconds = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 30),
                    etag = table.Column<string>(type: "TEXT", nullable: true),
                    last_modified_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    last_success_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    last_failure_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    created_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_epg_sources", x => x.epg_source_id);
                    table.ForeignKey(
                        name: "FK_epg_sources_providers_provider_id",
                        column: x => x.provider_id,
                        principalTable: "providers",
                        principalColumn: "provider_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "epg_channel_mappings",
                columns: table => new
                {
                    epg_channel_mapping_id = table.Column<string>(type: "TEXT", nullable: false),
                    profile_id = table.Column<string>(type: "TEXT", nullable: false),
                    provider_channel_id = table.Column<string>(type: "TEXT", nullable: false),
                    epg_source_id = table.Column<string>(type: "TEXT", nullable: false),
                    xmltv_channel_id = table.Column<string>(type: "TEXT", nullable: false),
                    mapping_mode = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "auto_id"),
                    confidence = table.Column<float>(type: "REAL", nullable: false, defaultValue: 1f),
                    created_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_epg_channel_mappings", x => x.epg_channel_mapping_id);
                    table.ForeignKey(
                        name: "FK_epg_channel_mappings_epg_sources_epg_source_id",
                        column: x => x.epg_source_id,
                        principalTable: "epg_sources",
                        principalColumn: "epg_source_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_epg_channel_mappings_profiles_profile_id",
                        column: x => x.profile_id,
                        principalTable: "profiles",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_epg_channel_mappings_provider_channels_provider_channel_id",
                        column: x => x.provider_channel_id,
                        principalTable: "provider_channels",
                        principalColumn: "provider_channel_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "epg_fetch_runs",
                columns: table => new
                {
                    epg_fetch_run_id = table.Column<string>(type: "TEXT", nullable: false),
                    epg_source_id = table.Column<string>(type: "TEXT", nullable: false),
                    started_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    finished_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    bytes = table.Column<int>(type: "INTEGER", nullable: true),
                    channel_count = table.Column<int>(type: "INTEGER", nullable: true),
                    programme_count = table.Column<int>(type: "INTEGER", nullable: true),
                    error_summary = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_epg_fetch_runs", x => x.epg_fetch_run_id);
                    table.ForeignKey(
                        name: "FK_epg_fetch_runs_epg_sources_epg_source_id",
                        column: x => x.epg_source_id,
                        principalTable: "epg_sources",
                        principalColumn: "epg_source_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "epg_source_channels",
                columns: table => new
                {
                    epg_source_channel_id = table.Column<string>(type: "TEXT", nullable: false),
                    epg_source_id = table.Column<string>(type: "TEXT", nullable: false),
                    xmltv_channel_id = table.Column<string>(type: "TEXT", nullable: false),
                    display_name = table.Column<string>(type: "TEXT", nullable: false),
                    icon_url = table.Column<string>(type: "TEXT", nullable: true),
                    last_seen_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_epg_source_channels", x => x.epg_source_channel_id);
                    table.ForeignKey(
                        name: "FK_epg_source_channels_epg_sources_epg_source_id",
                        column: x => x.epg_source_id,
                        principalTable: "epg_sources",
                        principalColumn: "epg_source_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_epg_channel_mappings_profile_channel_source",
                table: "epg_channel_mappings",
                columns: new[] { "profile_id", "provider_channel_id", "epg_source_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_epg_channel_mappings_epg_source_id",
                table: "epg_channel_mappings",
                column: "epg_source_id");

            migrationBuilder.CreateIndex(
                name: "IX_epg_channel_mappings_provider_channel_id",
                table: "epg_channel_mappings",
                column: "provider_channel_id");

            migrationBuilder.CreateIndex(
                name: "idx_epg_fetch_runs_source_time",
                table: "epg_fetch_runs",
                columns: new[] { "epg_source_id", "started_utc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "idx_epg_source_channels_source_channel",
                table: "epg_source_channels",
                columns: new[] { "epg_source_id", "xmltv_channel_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_epg_sources_provider",
                table: "epg_sources",
                column: "provider_id");

            migrationBuilder.CreateIndex(
                name: "idx_epg_sources_provider_priority",
                table: "epg_sources",
                columns: new[] { "provider_id", "priority" });

            // Backfill: create one epg_sources row for each provider that has an xmltv_url configured.
            // Xtream providers with XtreamIncludeXmltv get kind=provider_xmltv (URL resolved at fetch time).
            // Regular providers with xmltv_url get kind=xmltv_url with the URL stored directly.
            migrationBuilder.Sql(@"
INSERT INTO epg_sources (
    epg_source_id, provider_id, name, kind, url_or_path, priority, enabled,
    timeout_seconds, created_utc, updated_utc)
SELECT
    lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-' ||
    lower(hex(randomblob(2))) || '-' || lower(hex(randomblob(2))) || '-' ||
    lower(hex(randomblob(6))),
    provider_id,
    'Provider XMLTV',
    CASE WHEN xtream_base_url IS NOT NULL THEN 'provider_xmltv' ELSE 'xmltv_url' END,
    CASE WHEN xtream_base_url IS NULL THEN xmltv_url ELSE NULL END,
    1,
    1,
    30,
    datetime('now'),
    datetime('now')
FROM providers
WHERE xmltv_url IS NOT NULL
   OR (xtream_base_url IS NOT NULL AND xtream_include_xmltv = 1);
");

            // From Alpha4_StreamSettingsUi
            migrationBuilder.AddColumn<int>(
                name: "stream_buffer_max_bytes_hard_cap",
                table: "site_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 33554432);

            migrationBuilder.AddColumn<int>(
                name: "stream_buffer_max_bytes_per_session",
                table: "site_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 4194304);

            migrationBuilder.AddColumn<int>(
                name: "stream_buffer_read_chunk_size_bytes",
                table: "site_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 32768);

            migrationBuilder.AddColumn<int>(
                name: "stream_idle_grace_hard_cap_seconds",
                table: "site_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 120);

            migrationBuilder.AddColumn<int>(
                name: "stream_idle_grace_seconds",
                table: "site_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 15);

            migrationBuilder.AddColumn<int>(
                name: "stream_max_concurrent_sessions",
                table: "site_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 50);

            migrationBuilder.AddColumn<int>(
                name: "stream_reconnect_connect_timeout_seconds",
                table: "site_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 15);

            migrationBuilder.AddColumn<int>(
                name: "stream_reconnect_outage_window_seconds",
                table: "site_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 75);

            migrationBuilder.AddColumn<int>(
                name: "stream_reconnect_read_stall_timeout_seconds",
                table: "site_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<bool>(
                name: "streaming_settings_restart_required",
                table: "site_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse Alpha4_StreamSettingsUi
            migrationBuilder.DropColumn(
                name: "stream_buffer_max_bytes_hard_cap",
                table: "site_settings");

            migrationBuilder.DropColumn(
                name: "stream_buffer_max_bytes_per_session",
                table: "site_settings");

            migrationBuilder.DropColumn(
                name: "stream_buffer_read_chunk_size_bytes",
                table: "site_settings");

            migrationBuilder.DropColumn(
                name: "stream_idle_grace_hard_cap_seconds",
                table: "site_settings");

            migrationBuilder.DropColumn(
                name: "stream_idle_grace_seconds",
                table: "site_settings");

            migrationBuilder.DropColumn(
                name: "stream_max_concurrent_sessions",
                table: "site_settings");

            migrationBuilder.DropColumn(
                name: "stream_reconnect_connect_timeout_seconds",
                table: "site_settings");

            migrationBuilder.DropColumn(
                name: "stream_reconnect_outage_window_seconds",
                table: "site_settings");

            migrationBuilder.DropColumn(
                name: "stream_reconnect_read_stall_timeout_seconds",
                table: "site_settings");

            migrationBuilder.DropColumn(
                name: "streaming_settings_restart_required",
                table: "site_settings");

            // Reverse Alpha4_EpgSources
            migrationBuilder.DropTable(
                name: "epg_channel_mappings");

            migrationBuilder.DropTable(
                name: "epg_fetch_runs");

            migrationBuilder.DropTable(
                name: "epg_source_channels");

            migrationBuilder.DropTable(
                name: "epg_sources");

            // Reverse Alpha4_ProviderStreamLimits
            migrationBuilder.DropColumn(
                name: "max_concurrent_streams",
                table: "providers");

            // Reverse Alpha4_StreamingSettings
            migrationBuilder.DropColumn(
                name: "streaming_enabled",
                table: "site_settings");
        }
    }
}
