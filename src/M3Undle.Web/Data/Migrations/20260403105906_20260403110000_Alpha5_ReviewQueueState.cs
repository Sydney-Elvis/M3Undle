using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M3Undle.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class _20260403110000_Alpha5_ReviewQueueState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "decision",
                table: "profile_group_filters",
                type: "TEXT",
                nullable: false,
                defaultValue: "pending",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldDefaultValue: "hold");

            migrationBuilder.Sql(
                """
                UPDATE profile_group_filters
                SET decision = CASE
                    WHEN LOWER(TRIM(decision)) = 'hold' AND is_new = 1 THEN 'pending'
                    WHEN LOWER(TRIM(decision)) = 'hold' THEN 'include'
                    WHEN LOWER(TRIM(decision)) = 'exclude' THEN 'exclude'
                    WHEN LOWER(TRIM(decision)) = 'pending' THEN 'pending'
                    WHEN LOWER(TRIM(decision)) = 'include' THEN 'include'
                    ELSE 'include'
                END;
                """);

            migrationBuilder.Sql(
                """
                UPDATE profile_group_filters
                SET is_new = CASE WHEN decision = 'pending' THEN 1 ELSE 0 END;
                """);

            migrationBuilder.AddColumn<string>(
                name: "display_name_override",
                table: "profile_group_channel_filters",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "state",
                table: "profile_group_channel_filters",
                type: "TEXT",
                nullable: false,
                defaultValue: "included");

            migrationBuilder.AddColumn<DateTime>(
                name: "updated_utc",
                table: "profile_group_channel_filters",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.Sql(
                """
                UPDATE profile_group_channel_filters
                SET state = CASE
                    WHEN state IS NULL OR TRIM(state) = '' THEN 'included'
                    WHEN LOWER(TRIM(state)) IN ('pending', 'included', 'excluded') THEN LOWER(TRIM(state))
                    ELSE 'included'
                END;
                """);

            migrationBuilder.Sql(
                """
                UPDATE profile_group_channel_filters
                SET updated_utc = COALESCE(created_utc, CURRENT_TIMESTAMP)
                WHERE updated_utc IS NULL
                   OR updated_utc = '0001-01-01 00:00:00'
                   OR updated_utc = '0001-01-01 00:00:00.0000000'
                   OR updated_utc = '0001-01-01T00:00:00'
                   OR updated_utc = '0001-01-01T00:00:00.0000000';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE profile_group_filters
                SET is_new = CASE WHEN LOWER(TRIM(decision)) = 'pending' THEN 1 ELSE 0 END;
                """);

            migrationBuilder.Sql(
                """
                UPDATE profile_group_filters
                SET decision = CASE
                    WHEN LOWER(TRIM(decision)) = 'exclude' THEN 'exclude'
                    ELSE 'hold'
                END;
                """);

            migrationBuilder.DropColumn(
                name: "display_name_override",
                table: "profile_group_channel_filters");

            migrationBuilder.DropColumn(
                name: "state",
                table: "profile_group_channel_filters");

            migrationBuilder.DropColumn(
                name: "updated_utc",
                table: "profile_group_channel_filters");

            migrationBuilder.AlterColumn<string>(
                name: "decision",
                table: "profile_group_filters",
                type: "TEXT",
                nullable: false,
                defaultValue: "hold",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldDefaultValue: "pending");
        }
    }
}
