using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M3Undle.Web.Data.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260307000001_Alpha2_RemoveIncludeDecision")]
    public partial class Alpha2_RemoveIncludeDecision : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The "include" group decision is removed. Groups are now either "hold" (active) or "exclude".
            // Channel selection is the only way to put channels in the output.
            migrationBuilder.Sql("UPDATE profile_group_filters SET decision = 'hold' WHERE decision = 'include'");

            // The "all" channel mode (auto-include all channels) is removed. Always use "select" mode.
            migrationBuilder.Sql("UPDATE profile_group_filters SET channel_mode = 'select' WHERE channel_mode = 'all'");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No meaningful rollback — we cannot know which "hold" rows were originally "include".
        }
    }
}
