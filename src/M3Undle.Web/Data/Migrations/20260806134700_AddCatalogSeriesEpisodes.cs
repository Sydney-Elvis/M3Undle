using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M3Undle.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCatalogSeriesEpisodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "catalog_series_episodes",
                columns: table => new
                {
                    catalog_series_episode_id = table.Column<string>(type: "TEXT", nullable: false),
                    provider_id = table.Column<string>(type: "TEXT", nullable: false),
                    provider_group_id = table.Column<string>(type: "TEXT", nullable: false),
                    provider_item_key = table.Column<string>(type: "TEXT", nullable: false),
                    episode_key = table.Column<string>(type: "TEXT", nullable: false),
                    title = table.Column<string>(type: "TEXT", nullable: false),
                    stream_url = table.Column<string>(type: "TEXT", nullable: false),
                    first_seen_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    last_seen_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    active = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catalog_series_episodes", x => x.catalog_series_episode_id);
                    table.ForeignKey(
                        name: "FK_catalog_series_episodes_provider_groups_provider_group_id",
                        column: x => x.provider_group_id,
                        principalTable: "provider_groups",
                        principalColumn: "provider_group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_catalog_series_episodes_providers_provider_id",
                        column: x => x.provider_id,
                        principalTable: "providers",
                        principalColumn: "provider_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_catalog_series_episodes_group_item_episode_unique",
                table: "catalog_series_episodes",
                columns: new[] { "provider_group_id", "provider_item_key", "episode_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_catalog_series_episodes_provider_active",
                table: "catalog_series_episodes",
                columns: new[] { "provider_id", "active" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "catalog_series_episodes");
        }
    }
}
