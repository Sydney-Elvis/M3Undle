using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M3Undle.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class _20260403163000_Alpha5_DynamicEventTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "event_content_key",
                table: "provider_channels",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "event_slot_key",
                table: "provider_channels",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_placeholder",
                table: "provider_channels",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "tracking_keywords",
                table: "profile_group_filters",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tracking_policy",
                table: "profile_group_filters",
                type: "TEXT",
                nullable: false,
                defaultValue: "review");

            migrationBuilder.CreateIndex(
                name: "idx_provider_channels_event_content",
                table: "provider_channels",
                columns: new[] { "provider_id", "event_content_key" },
                filter: "event_content_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "idx_provider_channels_placeholder_active",
                table: "provider_channels",
                columns: new[] { "provider_id", "is_placeholder", "active" });

            migrationBuilder.CreateIndex(
                name: "idx_pgf_profile_tracking_policy",
                table: "profile_group_filters",
                columns: new[] { "profile_id", "tracking_policy" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_provider_channels_event_content",
                table: "provider_channels");

            migrationBuilder.DropIndex(
                name: "idx_provider_channels_placeholder_active",
                table: "provider_channels");

            migrationBuilder.DropIndex(
                name: "idx_pgf_profile_tracking_policy",
                table: "profile_group_filters");

            migrationBuilder.DropColumn(
                name: "event_content_key",
                table: "provider_channels");

            migrationBuilder.DropColumn(
                name: "event_slot_key",
                table: "provider_channels");

            migrationBuilder.DropColumn(
                name: "is_placeholder",
                table: "provider_channels");

            migrationBuilder.DropColumn(
                name: "tracking_keywords",
                table: "profile_group_filters");

            migrationBuilder.DropColumn(
                name: "tracking_policy",
                table: "profile_group_filters");
        }
    }
}
