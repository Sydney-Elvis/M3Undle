using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M3Undle.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Alpha2_Phase2_ChannelSelections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "channel_mode",
                table: "profile_group_filters",
                type: "TEXT",
                nullable: false,
                defaultValue: "all");

            migrationBuilder.CreateTable(
                name: "profile_group_channel_filters",
                columns: table => new
                {
                    profile_group_channel_filter_id = table.Column<string>(type: "TEXT", nullable: false),
                    profile_group_filter_id = table.Column<string>(type: "TEXT", nullable: false),
                    provider_channel_id = table.Column<string>(type: "TEXT", nullable: false),
                    created_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_profile_group_channel_filters", x => x.profile_group_channel_filter_id);
                    table.ForeignKey(
                        name: "FK_profile_group_channel_filters_profile_group_filters_profile_group_filter_id",
                        column: x => x.profile_group_filter_id,
                        principalTable: "profile_group_filters",
                        principalColumn: "profile_group_filter_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_profile_group_channel_filters_provider_channels_provider_channel_id",
                        column: x => x.provider_channel_id,
                        principalTable: "provider_channels",
                        principalColumn: "provider_channel_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_pgcf_filter_channel_unique",
                table: "profile_group_channel_filters",
                columns: new[] { "profile_group_filter_id", "provider_channel_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_profile_group_channel_filters_provider_channel_id",
                table: "profile_group_channel_filters",
                column: "provider_channel_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "profile_group_channel_filters");

            migrationBuilder.DropColumn(
                name: "channel_mode",
                table: "profile_group_filters");
        }
    }
}
