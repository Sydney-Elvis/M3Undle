using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M3Undle.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Alpha5_EventMetadataAndInterestRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
                name: "event_sport",
                table: "provider_channels",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "event_title",
                table: "provider_channels",
                type: "TEXT",
                nullable: true);

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
                name: "profile_event_interest_rules");

            migrationBuilder.DropColumn(
                name: "event_league",
                table: "provider_channels");

            migrationBuilder.DropColumn(
                name: "event_participants_json",
                table: "provider_channels");

            migrationBuilder.DropColumn(
                name: "event_sport",
                table: "provider_channels");

            migrationBuilder.DropColumn(
                name: "event_title",
                table: "provider_channels");
        }
    }
}
