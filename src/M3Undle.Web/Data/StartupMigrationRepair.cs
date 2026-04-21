using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Data;

internal static class StartupMigrationRepair
{
    private const string Alpha4SchemaMigrationId = "20260314145015_Alpha4_Schema";
    private const string Alpha5SchemaMigrationId = "20260322000000_Alpha5_Schema";
    private const string ProductVersion = "10.0.5";

    public static async Task RepairAlpha5PartialSchemaAsync(ApplicationDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync();

        if (!await TableExistsAsync(conn, "__EFMigrationsHistory"))
            return;

        if (await MigrationAppliedAsync(conn, Alpha5SchemaMigrationId)
            || !await MigrationAppliedAsync(conn, Alpha4SchemaMigrationId)
            || !await HasPartialAlpha5SchemaAsync(conn))
        {
            return;
        }

        await using var tx = await conn.BeginTransactionAsync();

        await EnsureColumnAsync(conn, tx, "providers", "force_mpegts", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(conn, tx, "snapshots", "change_class", "TEXT NULL");

        await EnsureColumnAsync(conn, tx, "site_settings", "generated_hls_enabled", "INTEGER NOT NULL DEFAULT 1");
        await EnsureColumnAsync(conn, tx, "site_settings", "generated_hls_ffmpeg_path", "TEXT NULL");
        await EnsureColumnAsync(conn, tx, "site_settings", "generated_hls_settings_restart_required", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(conn, tx, "site_settings", "hdhr_advertised_base_url", "TEXT NULL");
        await EnsureColumnAsync(conn, tx, "site_settings", "hdhr_discovery_enabled", "INTEGER NOT NULL DEFAULT 1");
        await EnsureColumnAsync(conn, tx, "site_settings", "hdhr_enabled", "INTEGER NOT NULL DEFAULT 1");
        await EnsureColumnAsync(conn, tx, "site_settings", "hdhr_friendly_name", "TEXT NULL");
        await EnsureColumnAsync(conn, tx, "site_settings", "hdhr_settings_restart_required", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(conn, tx, "site_settings", "hdhr_silicondust_discovery_enabled", "INTEGER NOT NULL DEFAULT 1");
        await EnsureColumnAsync(conn, tx, "site_settings", "hdhr_ssdp_enabled", "INTEGER NOT NULL DEFAULT 1");
        await EnsureColumnAsync(conn, tx, "site_settings", "hdhr_tuner_count_override", "INTEGER NULL");
        await EnsureColumnAsync(conn, tx, "site_settings", "refresh_schedule_kind", "TEXT NOT NULL DEFAULT '6h'");
        await EnsureColumnAsync(conn, tx, "site_settings", "refresh_startup_catchup", "INTEGER NOT NULL DEFAULT 1");

        await EnsureColumnAsync(conn, tx, "provider_channels", "event_content_key", "TEXT NULL");
        await EnsureColumnAsync(conn, tx, "provider_channels", "event_league", "TEXT NULL");
        await EnsureColumnAsync(conn, tx, "provider_channels", "event_participants_json", "TEXT NULL");
        await EnsureColumnAsync(conn, tx, "provider_channels", "event_slot_key", "TEXT NULL");
        await EnsureColumnAsync(conn, tx, "provider_channels", "event_sport", "TEXT NULL");
        await EnsureColumnAsync(conn, tx, "provider_channels", "event_title", "TEXT NULL");
        await EnsureColumnAsync(conn, tx, "provider_channels", "is_placeholder", "INTEGER NOT NULL DEFAULT 0");

        await EnsureColumnAsync(conn, tx, "profiles", "is_active", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(conn, tx, "profile_group_filters", "tracking_keywords", "TEXT NULL");
        await EnsureColumnAsync(conn, tx, "profile_group_filters", "tracking_policy", "TEXT NOT NULL DEFAULT 'review'");
        await EnsureColumnAsync(conn, tx, "profile_group_channel_filters", "display_name_override", "TEXT NULL");
        await EnsureColumnAsync(conn, tx, "profile_group_channel_filters", "state", "TEXT NOT NULL DEFAULT 'included'");
        await EnsureColumnAsync(conn, tx, "profile_group_channel_filters", "tvg_id_override", "TEXT NULL");
        await EnsureColumnAsync(conn, tx, "profile_group_channel_filters", "updated_utc", "TEXT NOT NULL DEFAULT '0001-01-01 00:00:00'");
        await EnsureColumnAsync(conn, tx, "epg_sources", "refresh_interval_hours", "INTEGER NULL");
        await EnsureColumnAsync(conn, tx, "AspNetUsers", "AdaptiveLockoutEscalated", "INTEGER NOT NULL DEFAULT 0");

        await ApplyAlpha5DataUpdatesAsync(conn, tx);
        await EnsureAlpha5TablesAsync(conn, tx);
        await EnsureAlpha5IndexesAsync(conn, tx);
        await RecordMigrationAsync(conn, tx, Alpha5SchemaMigrationId);

        await tx.CommitAsync();
    }

    private static async Task<bool> HasPartialAlpha5SchemaAsync(DbConnection conn)
        => await ColumnExistsAsync(conn, "site_settings", "hdhr_advertised_base_url")
           || await ColumnExistsAsync(conn, "site_settings", "generated_hls_enabled")
           || await ColumnExistsAsync(conn, "providers", "force_mpegts")
           || await ColumnExistsAsync(conn, "profiles", "is_active")
           || await TableExistsAsync(conn, "profile_custom_groups");

    private static async Task ApplyAlpha5DataUpdatesAsync(DbConnection conn, DbTransaction tx)
    {
        await ExecuteAsync(conn, tx, """
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

        await ExecuteAsync(conn, tx, """
            UPDATE profile_group_filters
            SET is_new = CASE WHEN decision = 'pending' THEN 1 ELSE 0 END;
            """);

        await ExecuteAsync(conn, tx, """
            UPDATE profile_group_filters
            SET decision = 'include'
            WHERE decision IN ('pending', 'hold');
            """);

        await ExecuteAsync(conn, tx, """
            UPDATE profile_group_channel_filters
            SET state = CASE
                WHEN state IS NULL OR TRIM(state) = '' THEN 'included'
                WHEN LOWER(TRIM(state)) IN ('pending', 'included', 'excluded') THEN LOWER(TRIM(state))
                ELSE 'included'
            END;
            """);

        await ExecuteAsync(conn, tx, """
            UPDATE profile_group_channel_filters
            SET updated_utc = COALESCE(created_utc, CURRENT_TIMESTAMP)
            WHERE updated_utc IS NULL
               OR updated_utc = '0001-01-01 00:00:00'
               OR updated_utc = '0001-01-01 00:00:00.0000000'
               OR updated_utc = '0001-01-01T00:00:00'
               OR updated_utc = '0001-01-01T00:00:00.0000000';
            """);

        if (await ColumnExistsAsync(conn, tx, "providers", "is_active"))
        {
            await ExecuteAsync(conn, tx, """
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
                AND NOT EXISTS (SELECT 1 FROM profiles WHERE is_active = 1);
                """);
        }

        await ExecuteAsync(conn, tx, """
            UPDATE site_settings
            SET generated_hls_enabled = 1,
                hdhr_discovery_enabled = 1,
                hdhr_enabled = 1,
                hdhr_silicondust_discovery_enabled = 1,
                hdhr_ssdp_enabled = 1,
                refresh_schedule_kind = '6h',
                refresh_startup_catchup = 1
            WHERE id = 1;
            """);
    }

    private static async Task EnsureAlpha5TablesAsync(DbConnection conn, DbTransaction tx)
    {
        await ExecuteAsync(conn, tx, """
            CREATE TABLE IF NOT EXISTS "downstream_integrations" (
                "downstream_integration_id" TEXT NOT NULL CONSTRAINT "PK_downstream_integrations" PRIMARY KEY,
                "profile_id" TEXT NULL,
                "name" TEXT NOT NULL,
                "kind" TEXT NOT NULL,
                "base_url" TEXT NOT NULL,
                "api_key_encrypted" TEXT NULL,
                "webhook_headers_json" TEXT NULL,
                "trigger_on_lineup_update" INTEGER NOT NULL DEFAULT 1,
                "trigger_on_guide_update" INTEGER NOT NULL DEFAULT 1,
                "enabled" INTEGER NOT NULL DEFAULT 1,
                "last_notified_utc" TEXT NULL,
                "last_notify_error" TEXT NULL,
                "created_utc" TEXT NOT NULL,
                "updated_utc" TEXT NOT NULL,
                CONSTRAINT "FK_downstream_integrations_profiles_profile_id" FOREIGN KEY ("profile_id") REFERENCES "profiles" ("profile_id") ON DELETE SET NULL
            );
            """);

        await ExecuteAsync(conn, tx, """
            CREATE TABLE IF NOT EXISTS "profile_custom_groups" (
                "custom_group_id" TEXT NOT NULL CONSTRAINT "PK_profile_custom_groups" PRIMARY KEY,
                "profile_id" TEXT NOT NULL,
                "name" TEXT NOT NULL,
                "decision" TEXT NOT NULL DEFAULT 'include',
                "channel_mode" TEXT NOT NULL DEFAULT 'select',
                "tracking_policy" TEXT NOT NULL DEFAULT 'review',
                "tracking_keywords" TEXT NULL,
                "auto_num_start" INTEGER NULL,
                "auto_num_end" INTEGER NULL,
                "track_new_channels" INTEGER NOT NULL DEFAULT 0,
                "sort_override" INTEGER NULL,
                "created_utc" TEXT NOT NULL,
                "updated_utc" TEXT NOT NULL,
                CONSTRAINT "FK_profile_custom_groups_profiles_profile_id" FOREIGN KEY ("profile_id") REFERENCES "profiles" ("profile_id") ON DELETE CASCADE
            );
            """);

        await ExecuteAsync(conn, tx, """
            CREATE TABLE IF NOT EXISTS "profile_event_interest_rules" (
                "rule_id" TEXT NOT NULL CONSTRAINT "PK_profile_event_interest_rules" PRIMARY KEY,
                "profile_id" TEXT NOT NULL,
                "provider_id" TEXT NULL,
                "provider_group_id" TEXT NULL,
                "enabled" INTEGER NOT NULL DEFAULT 1,
                "match_type" TEXT NOT NULL,
                "match_value" TEXT NOT NULL,
                "action" TEXT NOT NULL,
                "priority" INTEGER NOT NULL DEFAULT 100,
                "created_utc" TEXT NOT NULL,
                "updated_utc" TEXT NOT NULL,
                CONSTRAINT "FK_profile_event_interest_rules_profiles_profile_id" FOREIGN KEY ("profile_id") REFERENCES "profiles" ("profile_id") ON DELETE CASCADE,
                CONSTRAINT "FK_profile_event_interest_rules_provider_groups_provider_group_id" FOREIGN KEY ("provider_group_id") REFERENCES "provider_groups" ("provider_group_id") ON DELETE SET NULL,
                CONSTRAINT "FK_profile_event_interest_rules_providers_provider_id" FOREIGN KEY ("provider_id") REFERENCES "providers" ("provider_id") ON DELETE SET NULL
            );
            """);

        await ExecuteAsync(conn, tx, """
            CREATE TABLE IF NOT EXISTS "profile_custom_group_channels" (
                "custom_group_channel_id" TEXT NOT NULL CONSTRAINT "PK_profile_custom_group_channels" PRIMARY KEY,
                "custom_group_id" TEXT NOT NULL,
                "provider_channel_id" TEXT NOT NULL,
                "state" TEXT NOT NULL DEFAULT 'included',
                "channel_number" INTEGER NULL,
                "display_name_override" TEXT NULL,
                "tvg_id_override" TEXT NULL,
                "created_utc" TEXT NOT NULL,
                "updated_utc" TEXT NOT NULL,
                CONSTRAINT "FK_profile_custom_group_channels_profile_custom_groups_custom_group_id" FOREIGN KEY ("custom_group_id") REFERENCES "profile_custom_groups" ("custom_group_id") ON DELETE CASCADE,
                CONSTRAINT "FK_profile_custom_group_channels_provider_channels_provider_channel_id" FOREIGN KEY ("provider_channel_id") REFERENCES "provider_channels" ("provider_channel_id") ON DELETE CASCADE
            );
            """);

        await ExecuteAsync(conn, tx, """
            CREATE TABLE IF NOT EXISTS "profile_custom_group_provider_links" (
                "link_id" TEXT NOT NULL CONSTRAINT "PK_profile_custom_group_provider_links" PRIMARY KEY,
                "custom_group_id" TEXT NOT NULL,
                "provider_group_id" TEXT NOT NULL,
                "created_utc" TEXT NOT NULL,
                CONSTRAINT "FK_profile_custom_group_provider_links_profile_custom_groups_custom_group_id" FOREIGN KEY ("custom_group_id") REFERENCES "profile_custom_groups" ("custom_group_id") ON DELETE CASCADE,
                CONSTRAINT "FK_profile_custom_group_provider_links_provider_groups_provider_group_id" FOREIGN KEY ("provider_group_id") REFERENCES "provider_groups" ("provider_group_id") ON DELETE CASCADE
            );
            """);
    }

    private static async Task EnsureAlpha5IndexesAsync(DbConnection conn, DbTransaction tx)
    {
        await DropIndexIfExistsAsync(conn, tx, "idx_providers_is_active");
        await EnsureIndexAsync(conn, tx, "idx_provider_channels_event_content", """
            CREATE INDEX "idx_provider_channels_event_content"
            ON "provider_channels" ("provider_id", "event_content_key")
            WHERE event_content_key IS NOT NULL;
            """);
        await EnsureIndexAsync(conn, tx, "idx_provider_channels_placeholder_active", """
            CREATE INDEX "idx_provider_channels_placeholder_active"
            ON "provider_channels" ("provider_id", "is_placeholder", "active");
            """);
        await EnsureIndexAsync(conn, tx, "idx_profiles_is_active", """
            CREATE UNIQUE INDEX "idx_profiles_is_active"
            ON "profiles" ("is_active")
            WHERE is_active = 1;
            """);
        await EnsureIndexAsync(conn, tx, "idx_pgf_profile_tracking_policy", """
            CREATE INDEX "idx_pgf_profile_tracking_policy"
            ON "profile_group_filters" ("profile_id", "tracking_policy");
            """);
        await EnsureIndexAsync(conn, tx, "idx_downstream_integrations_profile", """
            CREATE INDEX "idx_downstream_integrations_profile"
            ON "downstream_integrations" ("profile_id");
            """);
        await EnsureIndexAsync(conn, tx, "idx_pcgc_group_channel_unique", """
            CREATE UNIQUE INDEX "idx_pcgc_group_channel_unique"
            ON "profile_custom_group_channels" ("custom_group_id", "provider_channel_id");
            """);
        await EnsureIndexAsync(conn, tx, "idx_pcgc_group_state", """
            CREATE INDEX "idx_pcgc_group_state"
            ON "profile_custom_group_channels" ("custom_group_id", "state");
            """);
        await EnsureIndexAsync(conn, tx, "IX_profile_custom_group_channels_provider_channel_id", """
            CREATE INDEX "IX_profile_custom_group_channels_provider_channel_id"
            ON "profile_custom_group_channels" ("provider_channel_id");
            """);
        await EnsureIndexAsync(conn, tx, "idx_pcgpl_group_provider_unique", """
            CREATE UNIQUE INDEX "idx_pcgpl_group_provider_unique"
            ON "profile_custom_group_provider_links" ("custom_group_id", "provider_group_id");
            """);
        await EnsureIndexAsync(conn, tx, "IX_profile_custom_group_provider_links_provider_group_id", """
            CREATE INDEX "IX_profile_custom_group_provider_links_provider_group_id"
            ON "profile_custom_group_provider_links" ("provider_group_id");
            """);
        await EnsureIndexAsync(conn, tx, "idx_pcg_profile_id", """
            CREATE INDEX "idx_pcg_profile_id"
            ON "profile_custom_groups" ("profile_id");
            """);
        await EnsureIndexAsync(conn, tx, "idx_pcg_profile_name_unique", """
            CREATE UNIQUE INDEX "idx_pcg_profile_name_unique"
            ON "profile_custom_groups" ("profile_id", "name");
            """);
        await EnsureIndexAsync(conn, tx, "idx_peir_profile_enabled_priority", """
            CREATE INDEX "idx_peir_profile_enabled_priority"
            ON "profile_event_interest_rules" ("profile_id", "enabled", "priority");
            """);
        await EnsureIndexAsync(conn, tx, "IX_profile_event_interest_rules_provider_group_id", """
            CREATE INDEX "IX_profile_event_interest_rules_provider_group_id"
            ON "profile_event_interest_rules" ("provider_group_id");
            """);
        await EnsureIndexAsync(conn, tx, "IX_profile_event_interest_rules_provider_id", """
            CREATE INDEX "IX_profile_event_interest_rules_provider_id"
            ON "profile_event_interest_rules" ("provider_id");
            """);
    }

    private static async Task EnsureColumnAsync(
        DbConnection conn,
        DbTransaction tx,
        string table,
        string column,
        string definition)
    {
        if (await ColumnExistsAsync(conn, tx, table, column))
            return;

        await ExecuteAsync(conn, tx, $"ALTER TABLE {QuoteIdentifier(table)} ADD COLUMN {QuoteIdentifier(column)} {definition};");
    }

    private static async Task EnsureIndexAsync(DbConnection conn, DbTransaction tx, string indexName, string createSql)
    {
        if (!await IndexExistsAsync(conn, tx, indexName))
            await ExecuteAsync(conn, tx, createSql);
    }

    private static async Task DropIndexIfExistsAsync(DbConnection conn, DbTransaction tx, string indexName)
    {
        if (await IndexExistsAsync(conn, tx, indexName))
            await ExecuteAsync(conn, tx, $"DROP INDEX {QuoteIdentifier(indexName)};");
    }

    private static async Task RecordMigrationAsync(DbConnection conn, DbTransaction tx, string migrationId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
            VALUES (@id, @version);
            """;
        var id = cmd.CreateParameter();
        id.ParameterName = "@id";
        id.Value = migrationId;
        cmd.Parameters.Add(id);

        var version = cmd.CreateParameter();
        version.ParameterName = "@version";
        version.Value = ProductVersion;
        cmd.Parameters.Add(version);

        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<bool> MigrationAppliedAsync(DbConnection conn, string migrationId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*)
            FROM "__EFMigrationsHistory"
            WHERE "MigrationId" = @id;
            """;
        var id = cmd.CreateParameter();
        id.ParameterName = "@id";
        id.Value = migrationId;
        cmd.Parameters.Add(id);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync()) > 0;
    }

    private static async Task<bool> TableExistsAsync(DbConnection conn, string table)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name;";
        var p = cmd.CreateParameter();
        p.ParameterName = "@name";
        p.Value = table;
        cmd.Parameters.Add(p);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync()) > 0;
    }

    private static Task<bool> ColumnExistsAsync(DbConnection conn, string table, string column)
        => ColumnExistsAsync(conn, null, table, column);

    private static async Task<bool> ColumnExistsAsync(DbConnection conn, DbTransaction? tx, string table, string column)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info({QuoteLiteral(table)}) WHERE name=@name;";
        var p = cmd.CreateParameter();
        p.ParameterName = "@name";
        p.Value = column;
        cmd.Parameters.Add(p);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync()) > 0;
    }

    private static async Task<bool> IndexExistsAsync(DbConnection conn, DbTransaction tx, string indexName)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name=@name;";
        var p = cmd.CreateParameter();
        p.ParameterName = "@name";
        p.Value = indexName;
        cmd.Parameters.Add(p);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync()) > 0;
    }

    private static async Task ExecuteAsync(DbConnection conn, DbTransaction tx, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static string QuoteIdentifier(string value)
        => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string QuoteLiteral(string value)
        => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
