using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M3Undle.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Alpha6_SystemEventIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_system_events_event_type_integration_id",
                table: "system_events",
                columns: new[] { "event_type", "integration_id" },
                filter: "\"integration_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_system_events_event_type_provider_id",
                table: "system_events",
                columns: new[] { "event_type", "provider_id" },
                filter: "\"provider_id\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_system_events_event_type_integration_id",
                table: "system_events");

            migrationBuilder.DropIndex(
                name: "ix_system_events_event_type_provider_id",
                table: "system_events");
        }
    }
}
