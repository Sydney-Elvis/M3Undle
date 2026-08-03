using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M3Undle.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProfileCatalogGroupFilters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "profile_catalog_group_filters",
                columns: table => new
                {
                    profile_catalog_group_filter_id = table.Column<string>(type: "TEXT", nullable: false),
                    profile_id = table.Column<string>(type: "TEXT", nullable: false),
                    provider_group_id = table.Column<string>(type: "TEXT", nullable: false),
                    decision = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "include"),
                    is_new = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    created_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_profile_catalog_group_filters", x => x.profile_catalog_group_filter_id);
                    table.ForeignKey(
                        name: "FK_profile_catalog_group_filters_profiles_profile_id",
                        column: x => x.profile_id,
                        principalTable: "profiles",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_profile_catalog_group_filters_provider_groups_provider_group_id",
                        column: x => x.provider_group_id,
                        principalTable: "provider_groups",
                        principalColumn: "provider_group_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_pcgf_profile_decision",
                table: "profile_catalog_group_filters",
                columns: new[] { "profile_id", "decision" });

            migrationBuilder.CreateIndex(
                name: "idx_pcgf_profile_group_unique",
                table: "profile_catalog_group_filters",
                columns: new[] { "profile_id", "provider_group_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_profile_catalog_group_filters_provider_group_id",
                table: "profile_catalog_group_filters",
                column: "provider_group_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "profile_catalog_group_filters");
        }
    }
}
