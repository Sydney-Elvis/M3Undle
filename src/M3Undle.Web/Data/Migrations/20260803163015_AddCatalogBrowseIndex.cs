using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M3Undle.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCatalogBrowseIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "catalog_items",
                columns: table => new
                {
                    catalog_item_id = table.Column<string>(type: "TEXT", nullable: false),
                    provider_id = table.Column<string>(type: "TEXT", nullable: false),
                    provider_group_id = table.Column<string>(type: "TEXT", nullable: false),
                    provider_item_key = table.Column<string>(type: "TEXT", nullable: false),
                    content_type = table.Column<string>(type: "TEXT", nullable: false),
                    title = table.Column<string>(type: "TEXT", nullable: false),
                    episode_count = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    first_seen_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    last_seen_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    active = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catalog_items", x => x.catalog_item_id);
                    table.ForeignKey(
                        name: "FK_catalog_items_provider_groups_provider_group_id",
                        column: x => x.provider_group_id,
                        principalTable: "provider_groups",
                        principalColumn: "provider_group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_catalog_items_providers_provider_id",
                        column: x => x.provider_id,
                        principalTable: "providers",
                        principalColumn: "provider_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_catalog_items_group_active_title",
                table: "catalog_items",
                columns: new[] { "provider_group_id", "active", "title" });

            migrationBuilder.CreateIndex(
                name: "idx_catalog_items_group_item_unique",
                table: "catalog_items",
                columns: new[] { "provider_group_id", "provider_item_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_catalog_items_provider_type_active",
                table: "catalog_items",
                columns: new[] { "provider_id", "content_type", "active" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "catalog_items");
        }
    }
}
