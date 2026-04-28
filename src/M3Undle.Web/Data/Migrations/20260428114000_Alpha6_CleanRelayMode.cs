using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace M3Undle.Web.Data.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260428114000_Alpha6_CleanRelayMode")]
public partial class Alpha6_CleanRelayMode : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "clean_relay_mode",
            table: "providers",
            type: "TEXT",
            nullable: false,
            defaultValue: "off");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "clean_relay_mode",
            table: "providers");
    }
}
