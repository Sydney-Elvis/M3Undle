using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M3Undle.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Alpha6_XtreamDetection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "xtream_detected_capable",
                table: "providers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "xtream_detected_capable",
                table: "providers");
        }
    }
}
