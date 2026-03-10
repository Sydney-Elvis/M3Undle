using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M3Undle.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Alpha2_Schema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // provider_groups: add channel_count
            migrationBuilder.AddColumn<int>(
                name: "channel_count",
                table: "provider_groups",
                type: "INTEGER",
                nullable: true);

            // provider_groups: add content_type
            migrationBuilder.AddColumn<string>(
                name: "content_type",
                table: "provider_groups",
                type: "TEXT",
                nullable: false,
                defaultValue: "live");

            // provider_channels: add content_type
            migrationBuilder.AddColumn<string>(
                name: "content_type",
                table: "provider_channels",
                type: "TEXT",
                nullable: false,
                defaultValue: "live");

            // providers: add content type flags
            migrationBuilder.AddColumn<bool>(
                name: "include_series",
                table: "providers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "include_vod",
                table: "providers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // providers: add Xtream credentials
            migrationBuilder.AddColumn<string>(
                name: "xtream_base_url",
                table: "providers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "xtream_encrypted_password",
                table: "providers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "xtream_include_xmltv",
                table: "providers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "xtream_username",
                table: "providers",
                type: "TEXT",
                nullable: true);

            // snapshots: add per-content-type channel counts
            migrationBuilder.AddColumn<int>(
                name: "live_channel_count",
                table: "snapshots",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "series_channel_count",
                table: "snapshots",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "vod_channel_count",
                table: "snapshots",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // profile_group_filters: new table (includes all Alpha2 columns)
            migrationBuilder.CreateTable(
                name: "profile_group_filters",
                columns: table => new
                {
                    profile_group_filter_id = table.Column<string>(type: "TEXT", nullable: false),
                    profile_id = table.Column<string>(type: "TEXT", nullable: false),
                    provider_group_id = table.Column<string>(type: "TEXT", nullable: false),
                    decision = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "hold"),
                    output_name = table.Column<string>(type: "TEXT", nullable: true),
                    auto_num_start = table.Column<int>(type: "INTEGER", nullable: true),
                    auto_num_end = table.Column<int>(type: "INTEGER", nullable: true),
                    track_new_channels = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    sort_override = table.Column<int>(type: "INTEGER", nullable: true),
                    channel_mode = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "all"),
                    is_new = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    created_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_profile_group_filters", x => x.profile_group_filter_id);
                    table.ForeignKey(
                        name: "FK_profile_group_filters_profiles_profile_id",
                        column: x => x.profile_id,
                        principalTable: "profiles",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_profile_group_filters_provider_groups_provider_group_id",
                        column: x => x.provider_group_id,
                        principalTable: "provider_groups",
                        principalColumn: "provider_group_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_pgf_profile_decision",
                table: "profile_group_filters",
                columns: new[] { "profile_id", "decision" });

            migrationBuilder.CreateIndex(
                name: "idx_pgf_profile_group_unique",
                table: "profile_group_filters",
                columns: new[] { "profile_id", "provider_group_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_profile_group_filters_provider_group_id",
                table: "profile_group_filters",
                column: "provider_group_id");

            // profile_group_channel_filters: new table (includes all Alpha2 columns)
            migrationBuilder.CreateTable(
                name: "profile_group_channel_filters",
                columns: table => new
                {
                    profile_group_channel_filter_id = table.Column<string>(type: "TEXT", nullable: false),
                    profile_group_filter_id = table.Column<string>(type: "TEXT", nullable: false),
                    provider_channel_id = table.Column<string>(type: "TEXT", nullable: false),
                    channel_number = table.Column<int>(type: "INTEGER", nullable: true),
                    output_group_name = table.Column<string>(type: "TEXT", nullable: true),
                    created_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_profile_group_channel_filters", x => x.profile_group_channel_filter_id);
                    table.ForeignKey(
                        name: "FK_profile_group_channel_filters_profile_group_filters_profile_group_filter_id",
                        column: x => x.profile_group_filter_id,
                        principalTable: "profile_group_filters",
                        principalColumn: "profile_group_filter_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_profile_group_channel_filters_provider_channels_provider_channel_id",
                        column: x => x.provider_channel_id,
                        principalTable: "provider_channels",
                        principalColumn: "provider_channel_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_pgcf_filter_channel_unique",
                table: "profile_group_channel_filters",
                columns: new[] { "profile_group_filter_id", "provider_channel_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_profile_group_channel_filters_provider_channel_id",
                table: "profile_group_channel_filters",
                column: "provider_channel_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "profile_group_channel_filters");
            migrationBuilder.DropTable(name: "profile_group_filters");

            migrationBuilder.DropColumn(name: "vod_channel_count", table: "snapshots");
            migrationBuilder.DropColumn(name: "series_channel_count", table: "snapshots");
            migrationBuilder.DropColumn(name: "live_channel_count", table: "snapshots");

            migrationBuilder.DropColumn(name: "xtream_username", table: "providers");
            migrationBuilder.DropColumn(name: "xtream_include_xmltv", table: "providers");
            migrationBuilder.DropColumn(name: "xtream_encrypted_password", table: "providers");
            migrationBuilder.DropColumn(name: "xtream_base_url", table: "providers");
            migrationBuilder.DropColumn(name: "include_vod", table: "providers");
            migrationBuilder.DropColumn(name: "include_series", table: "providers");

            migrationBuilder.DropColumn(name: "content_type", table: "provider_channels");
            migrationBuilder.DropColumn(name: "content_type", table: "provider_groups");
            migrationBuilder.DropColumn(name: "channel_count", table: "provider_groups");
        }
    }
}
