using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M3Undle.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Alpha5_PendingModelFix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "channel_mode",
                table: "profile_group_filters",
                type: "TEXT",
                nullable: false,
                defaultValue: "select",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldDefaultValue: "all");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "channel_mode",
                table: "profile_group_filters",
                type: "TEXT",
                nullable: false,
                defaultValue: "all",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldDefaultValue: "select");
        }
    }
}
