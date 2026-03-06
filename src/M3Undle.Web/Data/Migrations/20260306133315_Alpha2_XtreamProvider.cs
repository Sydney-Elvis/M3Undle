using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M3Undle.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Alpha2_XtreamProvider : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "xtream_base_url",
                table: "providers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "xtream_encrypted_password",
                table: "providers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "xtream_include_xmltv",
                table: "providers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "xtream_username",
                table: "providers",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "xtream_base_url",
                table: "providers");

            migrationBuilder.DropColumn(
                name: "xtream_encrypted_password",
                table: "providers");

            migrationBuilder.DropColumn(
                name: "xtream_include_xmltv",
                table: "providers");

            migrationBuilder.DropColumn(
                name: "xtream_username",
                table: "providers");
        }
    }
}
