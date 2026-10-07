using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M3Undle.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEpgCheckEvidenceAndEventSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "epg_source_id",
                table: "system_events",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_check_status",
                table: "epg_sources",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "last_checked_utc",
                table: "epg_sources",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_system_events_event_type_epg_source_id",
                table: "system_events",
                columns: new[] { "event_type", "epg_source_id" },
                filter: "\"epg_source_id\" IS NOT NULL");

            // Existing EPG failure/recovery rows were keyed by provider, so which source owned them is
            // unknowable. Retire them rather than guess; current failures are rebuilt from the next real check.
            migrationBuilder.Sql(
                "DELETE FROM system_events WHERE event_type IN ('EpgFetchFailed', 'EpgBackOnline') AND epg_source_id IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_system_events_event_type_epg_source_id",
                table: "system_events");

            migrationBuilder.DropColumn(
                name: "epg_source_id",
                table: "system_events");

            migrationBuilder.DropColumn(
                name: "last_check_status",
                table: "epg_sources");

            migrationBuilder.DropColumn(
                name: "last_checked_utc",
                table: "epg_sources");
        }
    }
}
