using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M3Undle.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Alpha5_DownstreamIntegrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "downstream_integrations",
                columns: table => new
                {
                    downstream_integration_id = table.Column<string>(type: "TEXT", nullable: false),
                    profile_id = table.Column<string>(type: "TEXT", nullable: true),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    base_url = table.Column<string>(type: "TEXT", nullable: false),
                    api_key_encrypted = table.Column<string>(type: "TEXT", nullable: true),
                    webhook_headers_json = table.Column<string>(type: "TEXT", nullable: true),
                    trigger_on_lineup_update = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    trigger_on_guide_update = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    last_notified_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    last_notify_error = table.Column<string>(type: "TEXT", nullable: true),
                    created_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_downstream_integrations", x => x.downstream_integration_id);
                    table.ForeignKey(
                        name: "FK_downstream_integrations_profiles_profile_id",
                        column: x => x.profile_id,
                        principalTable: "profiles",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "idx_downstream_integrations_profile",
                table: "downstream_integrations",
                column: "profile_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "downstream_integrations");
        }
    }
}
