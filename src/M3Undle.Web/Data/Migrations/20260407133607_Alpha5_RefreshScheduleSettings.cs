using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M3Undle.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Alpha5_RefreshScheduleSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "refresh_schedule_kind",
                table: "site_settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "6h");

            migrationBuilder.AddColumn<bool>(
                name: "refresh_startup_catchup",
                table: "site_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "refresh_interval_hours",
                table: "epg_sources",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "site_settings",
                keyColumn: "id",
                keyValue: 1,
                columns: new[] { "refresh_schedule_kind", "refresh_startup_catchup" },
                values: new object[] { "6h", true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "refresh_schedule_kind",
                table: "site_settings");

            migrationBuilder.DropColumn(
                name: "refresh_startup_catchup",
                table: "site_settings");

            migrationBuilder.DropColumn(
                name: "refresh_interval_hours",
                table: "epg_sources");
        }
    }
}
