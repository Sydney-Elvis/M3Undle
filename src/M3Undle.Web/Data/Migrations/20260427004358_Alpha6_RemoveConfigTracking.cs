using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M3Undle.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Alpha6_RemoveConfigTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "config_source_path",
                table: "providers");

            migrationBuilder.DropColumn(
                name: "needs_env_var_substitution",
                table: "providers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "config_source_path",
                table: "providers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "needs_env_var_substitution",
                table: "providers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }
    }
}
