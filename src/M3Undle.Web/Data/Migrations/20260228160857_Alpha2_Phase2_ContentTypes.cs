using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M3Undle.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Alpha2_Phase2_ContentTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

            migrationBuilder.AddColumn<string>(
                name: "content_type",
                table: "provider_groups",
                type: "TEXT",
                nullable: false,
                defaultValue: "live");

            migrationBuilder.AddColumn<string>(
                name: "content_type",
                table: "provider_channels",
                type: "TEXT",
                nullable: false,
                defaultValue: "live");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "include_series",
                table: "providers");

            migrationBuilder.DropColumn(
                name: "include_vod",
                table: "providers");

            migrationBuilder.DropColumn(
                name: "content_type",
                table: "provider_groups");

            migrationBuilder.DropColumn(
                name: "content_type",
                table: "provider_channels");
        }
    }
}
