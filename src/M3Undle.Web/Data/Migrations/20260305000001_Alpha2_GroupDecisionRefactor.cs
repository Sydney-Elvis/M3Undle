using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M3Undle.Web.Data.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260305000001_Alpha2_GroupDecisionRefactor")]
    /// <inheritdoc />
    public partial class Alpha2_GroupDecisionRefactor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add is_new column (default 0 = not new)
            migrationBuilder.AddColumn<bool>(
                name: "is_new",
                table: "profile_group_filters",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Rename 'pending' decision to 'hold'
            migrationBuilder.Sql("UPDATE profile_group_filters SET decision = 'hold' WHERE decision = 'pending'");

            // All rows that are currently 'hold' (i.e. were pending/unreviewed) get is_new = 1
            migrationBuilder.Sql("UPDATE profile_group_filters SET is_new = 1 WHERE decision = 'hold'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE profile_group_filters SET decision = 'pending' WHERE decision = 'hold'");

            migrationBuilder.DropColumn(
                name: "is_new",
                table: "profile_group_filters");
        }
    }
}
