using System;
using M3Undle.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M3Undle.Web.Data.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260404000000_Alpha6_ActiveProfile")]
    /// <inheritdoc />
    public partial class Alpha6_ActiveProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Add is_active to profiles
            migrationBuilder.AddColumn<bool>(
                name: "is_active",
                table: "profiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // 2. Add partial unique index on profiles.is_active
            migrationBuilder.CreateIndex(
                name: "idx_profiles_is_active",
                table: "profiles",
                column: "is_active",
                unique: true,
                filter: "is_active = 1");

            // 3. Data migration: find the currently-active provider, walk to its linked profile,
            //    and mark that profile as active. Uses a sub-select to stay within SQLite's limits.
            migrationBuilder.Sql("""
                UPDATE profiles
                SET is_active = 1
                WHERE profile_id = (
                    SELECT pp.profile_id
                    FROM profile_providers pp
                    INNER JOIN providers p ON p.provider_id = pp.provider_id
                    WHERE p.is_active = 1
                    ORDER BY pp.priority ASC
                    LIMIT 1
                )
                """);

            // 4+5. Remove is_active from providers via controlled SQLite rebuild.
            // Hardened with explicit transaction boundaries and post-rebuild FK verification.
            migrationBuilder.Sql("PRAGMA foreign_keys = 0;", suppressTransaction: true);
            migrationBuilder.Sql("""
                BEGIN IMMEDIATE;
                DROP TABLE IF EXISTS "providers_new";

                CREATE TABLE "providers_new" (
                    "provider_id" TEXT NOT NULL CONSTRAINT "PK_providers" PRIMARY KEY,
                    "config_source_path" TEXT NULL,
                    "created_utc" TEXT NOT NULL,
                    "enabled" INTEGER NOT NULL,
                    "headers_json" TEXT NULL,
                    "include_series" INTEGER NOT NULL DEFAULT 0,
                    "include_vod" INTEGER NOT NULL DEFAULT 0,
                    "max_concurrent_streams" INTEGER NULL,
                    "name" TEXT NOT NULL,
                    "needs_env_var_substitution" INTEGER NOT NULL,
                    "playlist_url" TEXT NOT NULL,
                    "timeout_seconds" INTEGER NOT NULL DEFAULT 20,
                    "updated_utc" TEXT NOT NULL,
                    "user_agent" TEXT NULL,
                    "xmltv_url" TEXT NULL,
                    "xtream_base_url" TEXT NULL,
                    "xtream_encrypted_password" TEXT NULL,
                    "xtream_include_xmltv" INTEGER NOT NULL DEFAULT 0,
                    "xtream_username" TEXT NULL
                );

                INSERT INTO "providers_new" (
                    "provider_id", "config_source_path", "created_utc", "enabled", "headers_json",
                    "include_series", "include_vod", "max_concurrent_streams", "name",
                    "needs_env_var_substitution", "playlist_url", "timeout_seconds", "updated_utc",
                    "user_agent", "xmltv_url", "xtream_base_url", "xtream_encrypted_password",
                    "xtream_include_xmltv", "xtream_username"
                )
                SELECT
                    "provider_id", "config_source_path", "created_utc", "enabled", "headers_json",
                    "include_series", "include_vod", "max_concurrent_streams", "name",
                    "needs_env_var_substitution", "playlist_url", "timeout_seconds", "updated_utc",
                    "user_agent", "xmltv_url", "xtream_base_url", "xtream_encrypted_password",
                    "xtream_include_xmltv", "xtream_username"
                FROM "providers";

                DROP TABLE "providers";
                ALTER TABLE "providers_new" RENAME TO "providers";

                CREATE INDEX "idx_providers_enabled" ON "providers" ("enabled");
                CREATE UNIQUE INDEX "IX_providers_name" ON "providers" ("name");
                COMMIT;
                """, suppressTransaction: true);
            migrationBuilder.Sql("PRAGMA foreign_keys = 1;", suppressTransaction: true);
            migrationBuilder.Sql("PRAGMA foreign_key_check;", suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_active",
                table: "providers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "idx_providers_is_active",
                table: "providers",
                column: "is_active",
                unique: true,
                filter: "is_active = 1");

            // Restore active provider from the active profile's first linked provider
            migrationBuilder.Sql("""
                UPDATE providers
                SET is_active = 1
                WHERE provider_id = (
                    SELECT pp.provider_id
                    FROM profile_providers pp
                    INNER JOIN profiles pr ON pr.profile_id = pp.profile_id
                    WHERE pr.is_active = 1
                    ORDER BY pp.priority ASC
                    LIMIT 1
                )
                """);

            migrationBuilder.DropIndex(
                name: "idx_profiles_is_active",
                table: "profiles");

            // Remove is_active from profiles via controlled SQLite rebuild.
            migrationBuilder.Sql("PRAGMA foreign_keys = 0;", suppressTransaction: true);
            migrationBuilder.Sql("""
                BEGIN IMMEDIATE;
                DROP TABLE IF EXISTS "profiles_new";

                CREATE TABLE "profiles_new" (
                    "profile_id" TEXT NOT NULL CONSTRAINT "PK_profiles" PRIMARY KEY,
                    "created_utc" TEXT NOT NULL,
                    "enabled" INTEGER NOT NULL,
                    "merge_mode" TEXT NOT NULL,
                    "name" TEXT NOT NULL,
                    "output_name" TEXT NOT NULL,
                    "updated_utc" TEXT NOT NULL
                );

                INSERT INTO "profiles_new" (
                    "profile_id", "created_utc", "enabled", "merge_mode", "name", "output_name", "updated_utc"
                )
                SELECT
                    "profile_id", "created_utc", "enabled", "merge_mode", "name", "output_name", "updated_utc"
                FROM "profiles";

                DROP TABLE "profiles";
                ALTER TABLE "profiles_new" RENAME TO "profiles";
                CREATE UNIQUE INDEX "IX_profiles_name" ON "profiles" ("name");
                COMMIT;
                """, suppressTransaction: true);
            migrationBuilder.Sql("PRAGMA foreign_keys = 1;", suppressTransaction: true);
            migrationBuilder.Sql("PRAGMA foreign_key_check;", suppressTransaction: true);
        }
    }
}
