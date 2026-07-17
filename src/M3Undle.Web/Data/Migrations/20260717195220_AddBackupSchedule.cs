using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M3Undle.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBackupSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "backup_last_run_utc",
                table: "site_settings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "backup_schedule_enabled",
                table: "site_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.UpdateData(
                table: "site_settings",
                keyColumn: "id",
                keyValue: 1,
                column: "backup_last_run_utc",
                value: null);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "backup_last_run_utc",
                table: "site_settings");

            migrationBuilder.DropColumn(
                name: "backup_schedule_enabled",
                table: "site_settings");
        }
    }
}
