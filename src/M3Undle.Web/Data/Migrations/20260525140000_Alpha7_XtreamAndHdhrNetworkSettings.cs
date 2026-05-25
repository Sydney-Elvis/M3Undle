using M3Undle.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M3Undle.Web.Data.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260525140000_Alpha7_XtreamAndHdhrNetworkSettings")]
    public partial class Alpha7_XtreamAndHdhrNetworkSettings : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "xtream_compatibility_enabled",
                table: "site_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "hdhr_allowed_networks",
                table: "site_settings",
                type: "TEXT",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "xtream_compatibility_enabled",
                table: "site_settings");

            migrationBuilder.DropColumn(
                name: "hdhr_allowed_networks",
                table: "site_settings");
        }
    }
}
