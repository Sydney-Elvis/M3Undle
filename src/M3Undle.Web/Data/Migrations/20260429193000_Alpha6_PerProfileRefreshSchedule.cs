using M3Undle.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M3Undle.Web.Data.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260429193000_Alpha6_PerProfileRefreshSchedule")]
    public partial class Alpha6_PerProfileRefreshSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "refresh_schedule_kind_override",
                table: "profiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "refresh_startup_catchup_override",
                table: "profiles",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "refresh_schedule_kind_override",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "refresh_startup_catchup_override",
                table: "profiles");
        }
    }
}
