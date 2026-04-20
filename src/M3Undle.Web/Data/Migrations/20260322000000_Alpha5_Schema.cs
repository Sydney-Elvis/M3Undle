using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M3Undle.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Alpha5_Schema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_providers_is_active",
                table: "providers");

            migrationBuilder.AddColumn<bool>(
                name: "force_mpegts",
                table: "providers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "change_class",
                table: "snapshots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "generated_hls_enabled",
                table: "site_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "generated_hls_ffmpeg_path",
                table: "site_settings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "generated_hls_settings_restart_required",
                table: "site_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "hdhr_advertised_base_url",
                table: "site_settings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "hdhr_discovery_enabled",
                table: "site_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "hdhr_enabled",
                table: "site_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "hdhr_friendly_name",
                table: "site_settings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "hdhr_settings_restart_required",
                table: "site_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "hdhr_silicondust_discovery_enabled",
                table: "site_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "hdhr_ssdp_enabled",
                table: "site_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "hdhr_tuner_count_override",
                table: "site_settings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "refresh_schedule_kind",
                table: "site_settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "6h");

            migrationBuilder.AddColumn<bool>(
                name: "refresh_startup_catchup",
                table: "site_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "event_content_key",
                table: "provider_channels",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "event_league",
                table: "provider_channels",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "event_participants_json",
                table: "provider_channels",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "event_slot_key",
                table: "provider_channels",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "event_sport",
                table: "provider_channels",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "event_title",
                table: "provider_channels",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_placeholder",
                table: "provider_channels",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "is_active",
                table: "profiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AlterColumn<string>(
                name: "decision",
                table: "profile_group_filters",
                type: "TEXT",
                nullable: false,
                defaultValue: "include",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldDefaultValue: "hold");

            migrationBuilder.AddColumn<string>(
                name: "tracking_keywords",
                table: "profile_group_filters",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tracking_policy",
                table: "profile_group_filters",
                type: "TEXT",
                nullable: false,
                defaultValue: "review");

            migrationBuilder.AddColumn<string>(
                name: "display_name_override",
                table: "profile_group_channel_filters",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "state",
                table: "profile_group_channel_filters",
                type: "TEXT",
                nullable: false,
                defaultValue: "included");

            migrationBuilder.AddColumn<string>(
                name: "tvg_id_override",
                table: "profile_group_channel_filters",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "updated_utc",
                table: "profile_group_channel_filters",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.Sql(
                """
                UPDATE profile_group_filters
                SET decision = CASE
                    WHEN LOWER(TRIM(decision)) = 'hold' AND is_new = 1 THEN 'pending'
                    WHEN LOWER(TRIM(decision)) = 'hold' THEN 'include'
                    WHEN LOWER(TRIM(decision)) = 'exclude' THEN 'exclude'
                    WHEN LOWER(TRIM(decision)) = 'pending' THEN 'pending'
                    WHEN LOWER(TRIM(decision)) = 'include' THEN 'include'
                    ELSE 'include'
                END;
                """);

            migrationBuilder.Sql(
                """
                UPDATE profile_group_filters
                SET is_new = CASE WHEN decision = 'pending' THEN 1 ELSE 0 END;
                """);

            migrationBuilder.Sql(
                """
                UPDATE profile_group_filters
                SET decision = 'include'
                WHERE decision IN ('pending', 'hold');
                """);

            migrationBuilder.Sql(
                """
                UPDATE profile_group_channel_filters
                SET state = CASE
                    WHEN state IS NULL OR TRIM(state) = '' THEN 'included'
                    WHEN LOWER(TRIM(state)) IN ('pending', 'included', 'excluded') THEN LOWER(TRIM(state))
                    ELSE 'included'
                END;
                """);

            migrationBuilder.Sql(
                """
                UPDATE profile_group_channel_filters
                SET updated_utc = COALESCE(created_utc, CURRENT_TIMESTAMP)
                WHERE updated_utc IS NULL
                   OR updated_utc = '0001-01-01 00:00:00'
                   OR updated_utc = '0001-01-01 00:00:00.0000000'
                   OR updated_utc = '0001-01-01T00:00:00'
                   OR updated_utc = '0001-01-01T00:00:00.0000000';
                """);

            migrationBuilder.Sql(
                """
                UPDATE profiles
                SET is_active = 1
                WHERE profile_id = (
                    SELECT pp.profile_id
                    FROM profile_providers pp
                    INNER JOIN providers p ON p.provider_id = pp.provider_id
                    WHERE p.is_active = 1
                    ORDER BY pp.priority ASC
                    LIMIT 1
                );
                """);

            migrationBuilder.DropColumn(
                name: "is_active",
                table: "providers");

            migrationBuilder.AddColumn<int>(
                name: "refresh_interval_hours",
                table: "epg_sources",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AdaptiveLockoutEscalated",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "downstream_integrations",
                columns: table => new
                {
                    downstream_integration_id = table.Column<string>(type: "TEXT", nullable: false),
                    profile_id = table.Column<string>(type: "TEXT", nullable: true),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    base_url = table.Column<string>(type: "TEXT", nullable: false),
                    api_key_encrypted = table.Column<string>(type: "TEXT", nullable: true),
                    webhook_headers_json = table.Column<string>(type: "TEXT", nullable: true),
                    trigger_on_lineup_update = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    trigger_on_guide_update = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    last_notified_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    last_notify_error = table.Column<string>(type: "TEXT", nullable: true),
                    created_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_downstream_integrations", x => x.downstream_integration_id);
                    table.ForeignKey(
                        name: "FK_downstream_integrations_profiles_profile_id",
                        column: x => x.profile_id,
                        principalTable: "profiles",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "profile_custom_groups",
                columns: table => new
                {
                    custom_group_id = table.Column<string>(type: "TEXT", nullable: false),
                    profile_id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    decision = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "include"),
                    channel_mode = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "select"),
                    tracking_policy = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "review"),
                    tracking_keywords = table.Column<string>(type: "TEXT", nullable: true),
                    auto_num_start = table.Column<int>(type: "INTEGER", nullable: true),
                    auto_num_end = table.Column<int>(type: "INTEGER", nullable: true),
                    track_new_channels = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    sort_override = table.Column<int>(type: "INTEGER", nullable: true),
                    created_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_profile_custom_groups", x => x.custom_group_id);
                    table.ForeignKey(
                        name: "FK_profile_custom_groups_profiles_profile_id",
                        column: x => x.profile_id,
                        principalTable: "profiles",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "profile_event_interest_rules",
                columns: table => new
                {
                    rule_id = table.Column<string>(type: "TEXT", nullable: false),
                    profile_id = table.Column<string>(type: "TEXT", nullable: false),
                    provider_id = table.Column<string>(type: "TEXT", nullable: true),
                    provider_group_id = table.Column<string>(type: "TEXT", nullable: true),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    match_type = table.Column<string>(type: "TEXT", nullable: false),
                    match_value = table.Column<string>(type: "TEXT", nullable: false),
                    action = table.Column<string>(type: "TEXT", nullable: false),
                    priority = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 100),
                    created_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_profile_event_interest_rules", x => x.rule_id);
                    table.ForeignKey(
                        name: "FK_profile_event_interest_rules_profiles_profile_id",
                        column: x => x.profile_id,
                        principalTable: "profiles",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_profile_event_interest_rules_provider_groups_provider_group_id",
                        column: x => x.provider_group_id,
                        principalTable: "provider_groups",
                        principalColumn: "provider_group_id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_profile_event_interest_rules_providers_provider_id",
                        column: x => x.provider_id,
                        principalTable: "providers",
                        principalColumn: "provider_id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "profile_custom_group_channels",
                columns: table => new
                {
                    custom_group_channel_id = table.Column<string>(type: "TEXT", nullable: false),
                    custom_group_id = table.Column<string>(type: "TEXT", nullable: false),
                    provider_channel_id = table.Column<string>(type: "TEXT", nullable: false),
                    state = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "included"),
                    channel_number = table.Column<int>(type: "INTEGER", nullable: true),
                    display_name_override = table.Column<string>(type: "TEXT", nullable: true),
                    tvg_id_override = table.Column<string>(type: "TEXT", nullable: true),
                    created_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_profile_custom_group_channels", x => x.custom_group_channel_id);
                    table.ForeignKey(
                        name: "FK_profile_custom_group_channels_profile_custom_groups_custom_group_id",
                        column: x => x.custom_group_id,
                        principalTable: "profile_custom_groups",
                        principalColumn: "custom_group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_profile_custom_group_channels_provider_channels_provider_channel_id",
                        column: x => x.provider_channel_id,
                        principalTable: "provider_channels",
                        principalColumn: "provider_channel_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "profile_custom_group_provider_links",
                columns: table => new
                {
                    link_id = table.Column<string>(type: "TEXT", nullable: false),
                    custom_group_id = table.Column<string>(type: "TEXT", nullable: false),
                    provider_group_id = table.Column<string>(type: "TEXT", nullable: false),
                    created_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_profile_custom_group_provider_links", x => x.link_id);
                    table.ForeignKey(
                        name: "FK_profile_custom_group_provider_links_profile_custom_groups_custom_group_id",
                        column: x => x.custom_group_id,
                        principalTable: "profile_custom_groups",
                        principalColumn: "custom_group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_profile_custom_group_provider_links_provider_groups_provider_group_id",
                        column: x => x.provider_group_id,
                        principalTable: "provider_groups",
                        principalColumn: "provider_group_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.UpdateData(
                table: "site_settings",
                keyColumn: "id",
                keyValue: 1,
                columns: new[] { "generated_hls_enabled", "generated_hls_ffmpeg_path", "hdhr_advertised_base_url", "hdhr_discovery_enabled", "hdhr_enabled", "hdhr_friendly_name", "hdhr_silicondust_discovery_enabled", "hdhr_ssdp_enabled", "hdhr_tuner_count_override", "refresh_schedule_kind", "refresh_startup_catchup" },
                values: new object[] { true, null, null, true, true, null, true, true, null, "6h", true });

            migrationBuilder.CreateIndex(
                name: "idx_provider_channels_event_content",
                table: "provider_channels",
                columns: new[] { "provider_id", "event_content_key" },
                filter: "event_content_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "idx_provider_channels_placeholder_active",
                table: "provider_channels",
                columns: new[] { "provider_id", "is_placeholder", "active" });

            migrationBuilder.CreateIndex(
                name: "idx_profiles_is_active",
                table: "profiles",
                column: "is_active",
                unique: true,
                filter: "is_active = 1");

            migrationBuilder.CreateIndex(
                name: "idx_pgf_profile_tracking_policy",
                table: "profile_group_filters",
                columns: new[] { "profile_id", "tracking_policy" });

            migrationBuilder.CreateIndex(
                name: "idx_downstream_integrations_profile",
                table: "downstream_integrations",
                column: "profile_id");

            migrationBuilder.CreateIndex(
                name: "idx_pcgc_group_channel_unique",
                table: "profile_custom_group_channels",
                columns: new[] { "custom_group_id", "provider_channel_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_pcgc_group_state",
                table: "profile_custom_group_channels",
                columns: new[] { "custom_group_id", "state" });

            migrationBuilder.CreateIndex(
                name: "IX_profile_custom_group_channels_provider_channel_id",
                table: "profile_custom_group_channels",
                column: "provider_channel_id");

            migrationBuilder.CreateIndex(
                name: "idx_pcgpl_group_provider_unique",
                table: "profile_custom_group_provider_links",
                columns: new[] { "custom_group_id", "provider_group_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_profile_custom_group_provider_links_provider_group_id",
                table: "profile_custom_group_provider_links",
                column: "provider_group_id");

            migrationBuilder.CreateIndex(
                name: "idx_pcg_profile_id",
                table: "profile_custom_groups",
                column: "profile_id");

            migrationBuilder.CreateIndex(
                name: "idx_pcg_profile_name_unique",
                table: "profile_custom_groups",
                columns: new[] { "profile_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_peir_profile_enabled_priority",
                table: "profile_event_interest_rules",
                columns: new[] { "profile_id", "enabled", "priority" });

            migrationBuilder.CreateIndex(
                name: "IX_profile_event_interest_rules_provider_group_id",
                table: "profile_event_interest_rules",
                column: "provider_group_id");

            migrationBuilder.CreateIndex(
                name: "IX_profile_event_interest_rules_provider_id",
                table: "profile_event_interest_rules",
                column: "provider_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "downstream_integrations");

            migrationBuilder.DropTable(
                name: "profile_custom_group_channels");

            migrationBuilder.DropTable(
                name: "profile_custom_group_provider_links");

            migrationBuilder.DropTable(
                name: "profile_event_interest_rules");

            migrationBuilder.DropTable(
                name: "profile_custom_groups");

            migrationBuilder.DropIndex(
                name: "idx_provider_channels_event_content",
                table: "provider_channels");

            migrationBuilder.DropIndex(
                name: "idx_provider_channels_placeholder_active",
                table: "provider_channels");

            migrationBuilder.DropIndex(
                name: "idx_profiles_is_active",
                table: "profiles");

            migrationBuilder.DropIndex(
                name: "idx_pgf_profile_tracking_policy",
                table: "profile_group_filters");

            migrationBuilder.DropColumn(
                name: "change_class",
                table: "snapshots");

            migrationBuilder.DropColumn(
                name: "generated_hls_enabled",
                table: "site_settings");

            migrationBuilder.DropColumn(
                name: "generated_hls_ffmpeg_path",
                table: "site_settings");

            migrationBuilder.DropColumn(
                name: "generated_hls_settings_restart_required",
                table: "site_settings");

            migrationBuilder.DropColumn(
                name: "hdhr_advertised_base_url",
                table: "site_settings");

            migrationBuilder.DropColumn(
                name: "hdhr_discovery_enabled",
                table: "site_settings");

            migrationBuilder.DropColumn(
                name: "hdhr_enabled",
                table: "site_settings");

            migrationBuilder.DropColumn(
                name: "hdhr_friendly_name",
                table: "site_settings");

            migrationBuilder.DropColumn(
                name: "hdhr_settings_restart_required",
                table: "site_settings");

            migrationBuilder.DropColumn(
                name: "hdhr_silicondust_discovery_enabled",
                table: "site_settings");

            migrationBuilder.DropColumn(
                name: "hdhr_ssdp_enabled",
                table: "site_settings");

            migrationBuilder.DropColumn(
                name: "hdhr_tuner_count_override",
                table: "site_settings");

            migrationBuilder.DropColumn(
                name: "refresh_schedule_kind",
                table: "site_settings");

            migrationBuilder.DropColumn(
                name: "refresh_startup_catchup",
                table: "site_settings");

            migrationBuilder.DropColumn(
                name: "event_content_key",
                table: "provider_channels");

            migrationBuilder.DropColumn(
                name: "event_league",
                table: "provider_channels");

            migrationBuilder.DropColumn(
                name: "event_participants_json",
                table: "provider_channels");

            migrationBuilder.DropColumn(
                name: "event_slot_key",
                table: "provider_channels");

            migrationBuilder.DropColumn(
                name: "event_sport",
                table: "provider_channels");

            migrationBuilder.DropColumn(
                name: "event_title",
                table: "provider_channels");

            migrationBuilder.DropColumn(
                name: "is_placeholder",
                table: "provider_channels");

            migrationBuilder.DropColumn(
                name: "is_active",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "tracking_keywords",
                table: "profile_group_filters");

            migrationBuilder.DropColumn(
                name: "tracking_policy",
                table: "profile_group_filters");

            migrationBuilder.DropColumn(
                name: "display_name_override",
                table: "profile_group_channel_filters");

            migrationBuilder.DropColumn(
                name: "state",
                table: "profile_group_channel_filters");

            migrationBuilder.DropColumn(
                name: "tvg_id_override",
                table: "profile_group_channel_filters");

            migrationBuilder.DropColumn(
                name: "updated_utc",
                table: "profile_group_channel_filters");

            migrationBuilder.DropColumn(
                name: "refresh_interval_hours",
                table: "epg_sources");

            migrationBuilder.DropColumn(
                name: "AdaptiveLockoutEscalated",
                table: "AspNetUsers");

            migrationBuilder.AddColumn<bool>(
                name: "is_active",
                table: "providers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "idx_providers_is_active",
                table: "providers",
                column: "is_active",
                unique: true,
                filter: "is_active = 1");

            migrationBuilder.Sql(
                """
                UPDATE providers
                SET is_active = 1
                WHERE provider_id = (
                    SELECT pp.provider_id
                    FROM profile_providers pp
                    INNER JOIN profiles pr ON pr.profile_id = pp.profile_id
                    WHERE pr.is_active = 1
                    ORDER BY pp.priority ASC
                    LIMIT 1
                );
                """);

            migrationBuilder.DropColumn(
                name: "force_mpegts",
                table: "providers");

            migrationBuilder.Sql(
                """
                UPDATE profile_group_filters
                SET decision = CASE
                    WHEN LOWER(TRIM(decision)) = 'exclude' THEN 'exclude'
                    ELSE 'hold'
                END;
                """);

            migrationBuilder.DropColumn(
                name: "is_active",
                table: "profiles");

            migrationBuilder.AlterColumn<string>(
                name: "decision",
                table: "profile_group_filters",
                type: "TEXT",
                nullable: false,
                defaultValue: "hold",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldDefaultValue: "include");
        }
    }
}
