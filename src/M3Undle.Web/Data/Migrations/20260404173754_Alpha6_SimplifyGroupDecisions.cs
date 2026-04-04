using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M3Undle.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Alpha6_SimplifyGroupDecisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "decision",
                table: "profile_group_filters",
                type: "TEXT",
                nullable: false,
                defaultValue: "include",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldDefaultValue: "pending");

            migrationBuilder.Sql("""
                UPDATE profile_group_filters
                SET decision = 'include'
                WHERE decision IN ('pending', 'hold')
                """);

            migrationBuilder.Sql("""
                UPDATE profile_custom_groups
                SET decision = 'include'
                WHERE decision = 'pending'
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "decision",
                table: "profile_group_filters",
                type: "TEXT",
                nullable: false,
                defaultValue: "pending",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldDefaultValue: "include");
        }
    }
}
