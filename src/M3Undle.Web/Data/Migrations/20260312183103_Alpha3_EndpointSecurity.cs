using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M3Undle.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Alpha3_EndpointSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "endpoint_security_enabled",
                table: "site_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "endpoint_credentials",
                columns: table => new
                {
                    endpoint_credential_id = table.Column<string>(type: "TEXT", nullable: false),
                    username = table.Column<string>(type: "TEXT", nullable: false),
                    normalized_username = table.Column<string>(type: "TEXT", nullable: false),
                    password_hash = table.Column<string>(type: "TEXT", nullable: false),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    auth_type = table.Column<string>(type: "TEXT", nullable: false),
                    created_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_endpoint_credentials", x => x.endpoint_credential_id);
                });

            migrationBuilder.CreateTable(
                name: "endpoint_access_bindings",
                columns: table => new
                {
                    endpoint_access_binding_id = table.Column<string>(type: "TEXT", nullable: false),
                    endpoint_credential_id = table.Column<string>(type: "TEXT", nullable: false),
                    active_profile_id = table.Column<string>(type: "TEXT", nullable: true),
                    default_profile_id = table.Column<string>(type: "TEXT", nullable: true),
                    virtual_tuner_id = table.Column<string>(type: "TEXT", nullable: true),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_endpoint_access_bindings", x => x.endpoint_access_binding_id);
                    table.ForeignKey(
                        name: "FK_endpoint_access_bindings_endpoint_credentials_endpoint_credential_id",
                        column: x => x.endpoint_credential_id,
                        principalTable: "endpoint_credentials",
                        principalColumn: "endpoint_credential_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_endpoint_access_bindings_profiles_active_profile_id",
                        column: x => x.active_profile_id,
                        principalTable: "profiles",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_endpoint_access_bindings_profiles_default_profile_id",
                        column: x => x.default_profile_id,
                        principalTable: "profiles",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "idx_endpoint_access_bindings_active_profile",
                table: "endpoint_access_bindings",
                column: "active_profile_id");

            migrationBuilder.CreateIndex(
                name: "idx_endpoint_access_bindings_credential",
                table: "endpoint_access_bindings",
                column: "endpoint_credential_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_endpoint_access_bindings_default_profile_id",
                table: "endpoint_access_bindings",
                column: "default_profile_id");

            migrationBuilder.CreateIndex(
                name: "IX_endpoint_credentials_normalized_username",
                table: "endpoint_credentials",
                column: "normalized_username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_endpoint_credentials_username",
                table: "endpoint_credentials",
                column: "username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "endpoint_access_bindings");

            migrationBuilder.DropTable(
                name: "endpoint_credentials");

            migrationBuilder.DropColumn(
                name: "endpoint_security_enabled",
                table: "site_settings");
        }
    }
}
