using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M3Undle.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class SplitProviderGroupsByContentType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_provider_groups_provider_id_raw_name",
                table: "provider_groups");

            migrationBuilder.CreateIndex(
                name: "IX_provider_groups_provider_id_raw_name_content_type",
                table: "provider_groups",
                columns: new[] { "provider_id", "raw_name", "content_type" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_provider_groups_provider_id_raw_name_content_type",
                table: "provider_groups");

            // The old model can represent only one row per provider/name. Prefer the live row
            // when split rows exist; deleting the others applies the configured FK cascade or
            // set-null behavior before the old unique index is restored.
            migrationBuilder.Sql(
                """
                DELETE FROM provider_groups
                WHERE provider_group_id IN (
                    SELECT provider_group_id
                    FROM (
                        SELECT provider_group_id,
                               ROW_NUMBER() OVER (
                                   PARTITION BY provider_id, raw_name
                                   ORDER BY CASE content_type
                                                WHEN 'live' THEN 0
                                                WHEN 'mixed' THEN 1
                                                WHEN 'vod' THEN 2
                                                WHEN 'series' THEN 3
                                                ELSE 4
                                            END,
                                            active DESC,
                                            first_seen_utc
                               ) AS duplicate_rank
                        FROM provider_groups
                    ) ranked_groups
                    WHERE duplicate_rank > 1
                );
                """);

            migrationBuilder.CreateIndex(
                name: "IX_provider_groups_provider_id_raw_name",
                table: "provider_groups",
                columns: new[] { "provider_id", "raw_name" },
                unique: true);
        }
    }
}
