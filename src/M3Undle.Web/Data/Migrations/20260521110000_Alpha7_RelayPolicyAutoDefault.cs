using M3Undle.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M3Undle.Web.Data.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260521110000_Alpha7_RelayPolicyAutoDefault")]
    public partial class Alpha7_RelayPolicyAutoDefault : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE providers SET clean_relay_mode = 'auto' WHERE clean_relay_mode = 'off';");

            migrationBuilder.Sql(
                "UPDATE providers SET clean_relay_mode = 'on' WHERE lower(clean_relay_mode) = 'remux';");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE providers SET clean_relay_mode = 'off' WHERE clean_relay_mode = 'auto';");
        }
    }
}
