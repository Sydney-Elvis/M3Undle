using System;
using M3Undle.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M3Undle.Web.Data.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260519093000_Alpha7_StreamChannelHealthEvents")]
    public partial class Alpha7_StreamChannelHealthEvents : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "stream_channel_health_events",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    provider_id = table.Column<string>(type: "TEXT", nullable: false),
                    provider_channel_id = table.Column<string>(type: "TEXT", nullable: false),
                    display_name = table.Column<string>(type: "TEXT", nullable: false),
                    event_kind = table.Column<string>(type: "TEXT", nullable: false),
                    event_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    session_id = table.Column<string>(type: "TEXT", nullable: true),
                    relay_mode = table.Column<string>(type: "TEXT", nullable: true),
                    route_classification = table.Column<string>(type: "TEXT", nullable: true),
                    upstream_failure_kind = table.Column<string>(type: "TEXT", nullable: true),
                    reconnect_attempt = table.Column<int>(type: "INTEGER", nullable: true),
                    stall_duration_ms = table.Column<double>(type: "REAL", nullable: true),
                    recovery_duration_ms = table.Column<double>(type: "REAL", nullable: true),
                    safe_start_wait_ms = table.Column<double>(type: "REAL", nullable: true),
                    output_held_ms = table.Column<double>(type: "REAL", nullable: true),
                    safe_start_kind = table.Column<string>(type: "TEXT", nullable: true),
                    client_disconnect_reason = table.Column<string>(type: "TEXT", nullable: true),
                    client_abort_after_recovery = table.Column<bool>(type: "INTEGER", nullable: false),
                    client_abort_after_recovery_delay_ms = table.Column<double>(type: "REAL", nullable: true),
                    forced_retune = table.Column<bool>(type: "INTEGER", nullable: false),
                    ts_sync_loss = table.Column<bool>(type: "INTEGER", nullable: false),
                    bytes_suppressed = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stream_channel_health_events", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_stream_channel_health_events_event_kind_event_utc",
                table: "stream_channel_health_events",
                columns: new[] { "event_kind", "event_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_stream_channel_health_events_provider_channel_event_utc",
                table: "stream_channel_health_events",
                columns: new[] { "provider_id", "provider_channel_id", "event_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_stream_channel_health_events_session_id",
                table: "stream_channel_health_events",
                column: "session_id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "stream_channel_health_events");
        }
    }
}
