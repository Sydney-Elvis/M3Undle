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

            migrationBuilder.CreateIndex(
                name: "IX_provider_groups_provider_id_raw_name",
                table: "provider_groups",
                columns: new[] { "provider_id", "raw_name" },
                unique: true);
        }
    }
}
