using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M3Undle.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class _20260403180000_Alpha5_CustomGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "profile_custom_group_channels");

            migrationBuilder.DropTable(
                name: "profile_custom_group_provider_links");

            migrationBuilder.DropTable(
                name: "profile_custom_groups");
        }
    }
}
