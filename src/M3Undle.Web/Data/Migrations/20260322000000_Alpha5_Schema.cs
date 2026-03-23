using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M3Undle.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Alpha5_Schema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Alpha5: tvg_id_override per channel
            migrationBuilder.AddColumn<string>(
                name: "tvg_id_override",
                table: "profile_group_channel_filters",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "tvg_id_override",
                table: "profile_group_channel_filters");
        }
    }
}
