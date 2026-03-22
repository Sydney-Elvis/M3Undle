using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M3Undle.Web.Data.Migrations
{
    public partial class Alpha4_StreamSettingsUi : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "stream_buffer_max_bytes_hard_cap",
                table: "site_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 33554432);

            migrationBuilder.AddColumn<int>(
                name: "stream_buffer_max_bytes_per_session",
                table: "site_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 4194304);

            migrationBuilder.AddColumn<int>(
                name: "stream_buffer_read_chunk_size_bytes",
                table: "site_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 32768);

            migrationBuilder.AddColumn<int>(
                name: "stream_idle_grace_hard_cap_seconds",
                table: "site_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 120);

            migrationBuilder.AddColumn<int>(
                name: "stream_idle_grace_seconds",
                table: "site_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 15);

            migrationBuilder.AddColumn<int>(
                name: "stream_max_concurrent_sessions",
                table: "site_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 50);

            migrationBuilder.AddColumn<int>(
                name: "stream_reconnect_connect_timeout_seconds",
                table: "site_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 15);

            migrationBuilder.AddColumn<int>(
                name: "stream_reconnect_outage_window_seconds",
                table: "site_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 75);

            migrationBuilder.AddColumn<int>(
                name: "stream_reconnect_read_stall_timeout_seconds",
                table: "site_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<bool>(
                name: "streaming_settings_restart_required",
                table: "site_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "stream_buffer_max_bytes_hard_cap",
                table: "site_settings");

            migrationBuilder.DropColumn(
                name: "stream_buffer_max_bytes_per_session",
                table: "site_settings");

            migrationBuilder.DropColumn(
                name: "stream_buffer_read_chunk_size_bytes",
                table: "site_settings");

            migrationBuilder.DropColumn(
                name: "stream_idle_grace_hard_cap_seconds",
                table: "site_settings");

            migrationBuilder.DropColumn(
                name: "stream_idle_grace_seconds",
                table: "site_settings");

            migrationBuilder.DropColumn(
                name: "stream_max_concurrent_sessions",
                table: "site_settings");

            migrationBuilder.DropColumn(
                name: "stream_reconnect_connect_timeout_seconds",
                table: "site_settings");

            migrationBuilder.DropColumn(
                name: "stream_reconnect_outage_window_seconds",
                table: "site_settings");

            migrationBuilder.DropColumn(
                name: "stream_reconnect_read_stall_timeout_seconds",
                table: "site_settings");

            migrationBuilder.DropColumn(
                name: "streaming_settings_restart_required",
                table: "site_settings");
        }
    }
}
