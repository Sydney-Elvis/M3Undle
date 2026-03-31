using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M3Undle.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Alpha4_HdhrSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "hdhr_advertised_base_url",
                table: "site_settings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "hdhr_discovery_enabled",
                table: "site_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "hdhr_enabled",
                table: "site_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "hdhr_settings_restart_required",
                table: "site_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "hdhr_silicondust_discovery_enabled",
                table: "site_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "hdhr_ssdp_enabled",
                table: "site_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "hdhr_tuner_count_override",
                table: "site_settings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "site_settings",
                keyColumn: "id",
                keyValue: 1,
                columns: new[] { "hdhr_advertised_base_url", "hdhr_discovery_enabled", "hdhr_enabled", "hdhr_silicondust_discovery_enabled", "hdhr_ssdp_enabled", "hdhr_tuner_count_override" },
                values: new object[] { null, true, true, true, true, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "hdhr_advertised_base_url",
                table: "site_settings");

            migrationBuilder.DropColumn(
                name: "hdhr_discovery_enabled",
                table: "site_settings");

            migrationBuilder.DropColumn(
                name: "hdhr_enabled",
                table: "site_settings");

            migrationBuilder.DropColumn(
                name: "hdhr_settings_restart_required",
                table: "site_settings");

            migrationBuilder.DropColumn(
                name: "hdhr_silicondust_discovery_enabled",
                table: "site_settings");

            migrationBuilder.DropColumn(
                name: "hdhr_ssdp_enabled",
                table: "site_settings");

            migrationBuilder.DropColumn(
                name: "hdhr_tuner_count_override",
                table: "site_settings");
        }
    }
}
