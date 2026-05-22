using M3Undle.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M3Undle.Web.Data.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260522120000_Alpha7_StreamHealthCleanWatch")]
    public partial class Alpha7_StreamHealthCleanWatch : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "clean_watch_duration_ms",
                table: "stream_channel_health_events",
                type: "REAL",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "clean_watch_duration_ms",
                table: "stream_channel_health_events");
        }
    }
}
