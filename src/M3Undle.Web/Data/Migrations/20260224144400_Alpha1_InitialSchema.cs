using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M3Undle.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Alpha1_InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AspNetRoles",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: true),
                    SecurityStamp = table.Column<string>(type: "TEXT", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "TEXT", nullable: true),
                    PhoneNumber = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "profiles",
                columns: table => new
                {
                    profile_id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    output_name = table.Column<string>(type: "TEXT", nullable: false),
                    merge_mode = table.Column<string>(type: "TEXT", nullable: false),
                    created_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_profiles", x => x.profile_id);
                });

            migrationBuilder.CreateTable(
                name: "providers",
                columns: table => new
                {
                    provider_id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    is_active = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    playlist_url = table.Column<string>(type: "TEXT", nullable: false),
                    xmltv_url = table.Column<string>(type: "TEXT", nullable: true),
                    headers_json = table.Column<string>(type: "TEXT", nullable: true),
                    user_agent = table.Column<string>(type: "TEXT", nullable: true),
                    timeout_seconds = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 20),
                    created_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    config_source_path = table.Column<string>(type: "TEXT", nullable: true),
                    needs_env_var_substitution = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_providers", x => x.provider_id);
                });

            migrationBuilder.CreateTable(
                name: "site_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false),
                    authentication_enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_site_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoleClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RoleId = table.Column<string>(type: "TEXT", nullable: false),
                    ClaimType = table.Column<string>(type: "TEXT", nullable: true),
                    ClaimValue = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoleClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetRoleClaims_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    ClaimType = table.Column<string>(type: "TEXT", nullable: true),
                    ClaimValue = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserLogins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ProviderKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "TEXT", nullable: true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserPasskeys",
                columns: table => new
                {
                    CredentialId = table.Column<byte[]>(type: "BLOB", maxLength: 1024, nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Data = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserPasskeys", x => x.CredentialId);
                    table.ForeignKey(
                        name: "FK_AspNetUserPasskeys_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserRoles",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    RoleId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    LoginProvider = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "canonical_channels",
                columns: table => new
                {
                    channel_id = table.Column<string>(type: "TEXT", nullable: false),
                    profile_id = table.Column<string>(type: "TEXT", nullable: false),
                    display_name = table.Column<string>(type: "TEXT", nullable: false),
                    channel_number = table.Column<int>(type: "INTEGER", nullable: false),
                    group_name = table.Column<string>(type: "TEXT", nullable: true),
                    logo_url = table.Column<string>(type: "TEXT", nullable: true),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    is_event = table.Column<bool>(type: "INTEGER", nullable: false),
                    event_policy = table.Column<string>(type: "TEXT", nullable: false),
                    notes = table.Column<string>(type: "TEXT", nullable: true),
                    created_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_canonical_channels", x => x.channel_id);
                    table.ForeignKey(
                        name: "FK_canonical_channels_profiles_profile_id",
                        column: x => x.profile_id,
                        principalTable: "profiles",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "snapshots",
                columns: table => new
                {
                    snapshot_id = table.Column<string>(type: "TEXT", nullable: false),
                    profile_id = table.Column<string>(type: "TEXT", nullable: false),
                    created_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    playlist_path = table.Column<string>(type: "TEXT", nullable: false),
                    xmltv_path = table.Column<string>(type: "TEXT", nullable: false),
                    channel_index_path = table.Column<string>(type: "TEXT", nullable: false),
                    status_json_path = table.Column<string>(type: "TEXT", nullable: false),
                    channel_count_published = table.Column<int>(type: "INTEGER", nullable: false),
                    error_summary = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_snapshots", x => x.snapshot_id);
                    table.ForeignKey(
                        name: "FK_snapshots_profiles_profile_id",
                        column: x => x.profile_id,
                        principalTable: "profiles",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "fetch_runs",
                columns: table => new
                {
                    fetch_run_id = table.Column<string>(type: "TEXT", nullable: false),
                    provider_id = table.Column<string>(type: "TEXT", nullable: false),
                    started_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    finished_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    type = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "snapshot"),
                    error_summary = table.Column<string>(type: "TEXT", nullable: true),
                    playlist_etag = table.Column<string>(type: "TEXT", nullable: true),
                    playlist_last_modified = table.Column<string>(type: "TEXT", nullable: true),
                    xmltv_etag = table.Column<string>(type: "TEXT", nullable: true),
                    xmltv_last_modified = table.Column<string>(type: "TEXT", nullable: true),
                    playlist_bytes = table.Column<int>(type: "INTEGER", nullable: true),
                    xmltv_bytes = table.Column<int>(type: "INTEGER", nullable: true),
                    channel_count_seen = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fetch_runs", x => x.fetch_run_id);
                    table.ForeignKey(
                        name: "FK_fetch_runs_providers_provider_id",
                        column: x => x.provider_id,
                        principalTable: "providers",
                        principalColumn: "provider_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "profile_providers",
                columns: table => new
                {
                    profile_id = table.Column<string>(type: "TEXT", nullable: false),
                    provider_id = table.Column<string>(type: "TEXT", nullable: false),
                    priority = table.Column<int>(type: "INTEGER", nullable: false),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_profile_providers", x => new { x.profile_id, x.provider_id });
                    table.ForeignKey(
                        name: "FK_profile_providers_profiles_profile_id",
                        column: x => x.profile_id,
                        principalTable: "profiles",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_profile_providers_providers_provider_id",
                        column: x => x.provider_id,
                        principalTable: "providers",
                        principalColumn: "provider_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "provider_groups",
                columns: table => new
                {
                    provider_group_id = table.Column<string>(type: "TEXT", nullable: false),
                    provider_id = table.Column<string>(type: "TEXT", nullable: false),
                    raw_name = table.Column<string>(type: "TEXT", nullable: false),
                    normalized_name = table.Column<string>(type: "TEXT", nullable: true),
                    first_seen_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    last_seen_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    active = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_groups", x => x.provider_group_id);
                    table.ForeignKey(
                        name: "FK_provider_groups_providers_provider_id",
                        column: x => x.provider_id,
                        principalTable: "providers",
                        principalColumn: "provider_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "channel_match_rules",
                columns: table => new
                {
                    rule_id = table.Column<string>(type: "TEXT", nullable: false),
                    profile_id = table.Column<string>(type: "TEXT", nullable: false),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    match_type = table.Column<string>(type: "TEXT", nullable: false),
                    match_value = table.Column<string>(type: "TEXT", nullable: false),
                    target_channel_id = table.Column<string>(type: "TEXT", nullable: true),
                    target_group_name = table.Column<string>(type: "TEXT", nullable: true),
                    default_priority = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 1),
                    is_event_rule = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_channel_match_rules", x => x.rule_id);
                    table.ForeignKey(
                        name: "FK_channel_match_rules_canonical_channels_target_channel_id",
                        column: x => x.target_channel_id,
                        principalTable: "canonical_channels",
                        principalColumn: "channel_id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_channel_match_rules_profiles_profile_id",
                        column: x => x.profile_id,
                        principalTable: "profiles",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "epg_channel_map",
                columns: table => new
                {
                    epg_map_id = table.Column<string>(type: "TEXT", nullable: false),
                    profile_id = table.Column<string>(type: "TEXT", nullable: false),
                    channel_id = table.Column<string>(type: "TEXT", nullable: false),
                    xmltv_channel_id = table.Column<string>(type: "TEXT", nullable: false),
                    source = table.Column<string>(type: "TEXT", nullable: false),
                    created_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_epg_channel_map", x => x.epg_map_id);
                    table.ForeignKey(
                        name: "FK_epg_channel_map_canonical_channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "canonical_channels",
                        principalColumn: "channel_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_epg_channel_map_profiles_profile_id",
                        column: x => x.profile_id,
                        principalTable: "profiles",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stream_keys",
                columns: table => new
                {
                    stream_key = table.Column<string>(type: "TEXT", nullable: false),
                    profile_id = table.Column<string>(type: "TEXT", nullable: false),
                    channel_id = table.Column<string>(type: "TEXT", nullable: false),
                    created_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    last_used_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    revoked = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stream_keys", x => x.stream_key);
                    table.ForeignKey(
                        name: "FK_stream_keys_canonical_channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "canonical_channels",
                        principalColumn: "channel_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_stream_keys_profiles_profile_id",
                        column: x => x.profile_id,
                        principalTable: "profiles",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "provider_channels",
                columns: table => new
                {
                    provider_channel_id = table.Column<string>(type: "TEXT", nullable: false),
                    provider_id = table.Column<string>(type: "TEXT", nullable: false),
                    provider_channel_key = table.Column<string>(type: "TEXT", nullable: true),
                    display_name = table.Column<string>(type: "TEXT", nullable: false),
                    tvg_id = table.Column<string>(type: "TEXT", nullable: true),
                    tvg_name = table.Column<string>(type: "TEXT", nullable: true),
                    logo_url = table.Column<string>(type: "TEXT", nullable: true),
                    stream_url = table.Column<string>(type: "TEXT", nullable: false),
                    group_title = table.Column<string>(type: "TEXT", nullable: true),
                    provider_group_id = table.Column<string>(type: "TEXT", nullable: true),
                    is_event = table.Column<bool>(type: "INTEGER", nullable: false),
                    event_start_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    event_end_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    first_seen_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    last_seen_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    active = table.Column<bool>(type: "INTEGER", nullable: false),
                    last_fetch_run_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_channels", x => x.provider_channel_id);
                    table.ForeignKey(
                        name: "FK_provider_channels_fetch_runs_last_fetch_run_id",
                        column: x => x.last_fetch_run_id,
                        principalTable: "fetch_runs",
                        principalColumn: "fetch_run_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_provider_channels_provider_groups_provider_group_id",
                        column: x => x.provider_group_id,
                        principalTable: "provider_groups",
                        principalColumn: "provider_group_id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_provider_channels_providers_provider_id",
                        column: x => x.provider_id,
                        principalTable: "providers",
                        principalColumn: "provider_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "channel_sources",
                columns: table => new
                {
                    channel_source_id = table.Column<string>(type: "TEXT", nullable: false),
                    channel_id = table.Column<string>(type: "TEXT", nullable: false),
                    provider_id = table.Column<string>(type: "TEXT", nullable: false),
                    provider_channel_id = table.Column<string>(type: "TEXT", nullable: false),
                    priority = table.Column<int>(type: "INTEGER", nullable: false),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    override_stream_url = table.Column<string>(type: "TEXT", nullable: true),
                    last_success_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    last_failure_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    failure_count_rolling = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    health_state = table.Column<string>(type: "TEXT", nullable: false),
                    created_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_channel_sources", x => x.channel_source_id);
                    table.ForeignKey(
                        name: "FK_channel_sources_canonical_channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "canonical_channels",
                        principalColumn: "channel_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_channel_sources_provider_channels_provider_channel_id",
                        column: x => x.provider_channel_id,
                        principalTable: "provider_channels",
                        principalColumn: "provider_channel_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_channel_sources_providers_provider_id",
                        column: x => x.provider_id,
                        principalTable: "providers",
                        principalColumn: "provider_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "site_settings",
                columns: new[] { "id", "authentication_enabled" },
                values: new object[] { 1, false });

            migrationBuilder.CreateIndex(
                name: "IX_AspNetRoleClaims_RoleId",
                table: "AspNetRoleClaims",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                table: "AspNetRoles",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserClaims_UserId",
                table: "AspNetUserClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserLogins_UserId",
                table: "AspNetUserLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserPasskeys_UserId",
                table: "AspNetUserPasskeys",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserRoles_RoleId",
                table: "AspNetUserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "AspNetUsers",
                column: "NormalizedUserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_canonical_channels_profile_enabled",
                table: "canonical_channels",
                columns: new[] { "profile_id", "enabled" });

            migrationBuilder.CreateIndex(
                name: "idx_canonical_channels_profile_number",
                table: "canonical_channels",
                columns: new[] { "profile_id", "channel_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_match_rules_profile",
                table: "channel_match_rules",
                columns: new[] { "profile_id", "enabled" });

            migrationBuilder.CreateIndex(
                name: "IX_channel_match_rules_target_channel_id",
                table: "channel_match_rules",
                column: "target_channel_id");

            migrationBuilder.CreateIndex(
                name: "idx_channel_sources_channel",
                table: "channel_sources",
                columns: new[] { "channel_id", "priority" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_channel_sources_health",
                table: "channel_sources",
                columns: new[] { "health_state", "last_failure_utc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_channel_sources_provider_channel_id",
                table: "channel_sources",
                column: "provider_channel_id");

            migrationBuilder.CreateIndex(
                name: "IX_channel_sources_provider_id",
                table: "channel_sources",
                column: "provider_id");

            migrationBuilder.CreateIndex(
                name: "idx_epg_map_profile",
                table: "epg_channel_map",
                columns: new[] { "profile_id", "xmltv_channel_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_epg_channel_map_channel_id",
                table: "epg_channel_map",
                column: "channel_id");

            migrationBuilder.CreateIndex(
                name: "IX_epg_channel_map_profile_id_channel_id",
                table: "epg_channel_map",
                columns: new[] { "profile_id", "channel_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_fetch_runs_provider_time",
                table: "fetch_runs",
                columns: new[] { "provider_id", "started_utc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "idx_fetch_runs_status",
                table: "fetch_runs",
                columns: new[] { "status", "started_utc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "idx_profile_providers_profile",
                table: "profile_providers",
                columns: new[] { "profile_id", "priority" });

            migrationBuilder.CreateIndex(
                name: "IX_profile_providers_provider_id",
                table: "profile_providers",
                column: "provider_id");

            migrationBuilder.CreateIndex(
                name: "IX_profiles_name",
                table: "profiles",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_provider_channels_is_event",
                table: "provider_channels",
                columns: new[] { "provider_id", "is_event", "event_start_utc" });

            migrationBuilder.CreateIndex(
                name: "idx_provider_channels_provider_active",
                table: "provider_channels",
                columns: new[] { "provider_id", "active" });

            migrationBuilder.CreateIndex(
                name: "idx_provider_channels_seen",
                table: "provider_channels",
                columns: new[] { "provider_id", "last_seen_utc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_provider_channels_last_fetch_run_id",
                table: "provider_channels",
                column: "last_fetch_run_id");

            migrationBuilder.CreateIndex(
                name: "IX_provider_channels_provider_group_id",
                table: "provider_channels",
                column: "provider_group_id");

            migrationBuilder.CreateIndex(
                name: "IX_provider_channels_provider_id_provider_channel_key",
                table: "provider_channels",
                columns: new[] { "provider_id", "provider_channel_key" },
                unique: true,
                filter: "provider_channel_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "idx_provider_groups_provider_active",
                table: "provider_groups",
                columns: new[] { "provider_id", "active" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_groups_provider_id_raw_name",
                table: "provider_groups",
                columns: new[] { "provider_id", "raw_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_providers_enabled",
                table: "providers",
                column: "enabled");

            migrationBuilder.CreateIndex(
                name: "idx_providers_is_active",
                table: "providers",
                column: "is_active",
                unique: true,
                filter: "is_active = 1");

            migrationBuilder.CreateIndex(
                name: "IX_providers_name",
                table: "providers",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_snapshots_profile_status",
                table: "snapshots",
                columns: new[] { "profile_id", "status", "created_utc" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "idx_stream_keys_channel",
                table: "stream_keys",
                column: "channel_id");

            migrationBuilder.CreateIndex(
                name: "idx_stream_keys_profile",
                table: "stream_keys",
                columns: new[] { "profile_id", "revoked" });

            migrationBuilder.CreateIndex(
                name: "IX_stream_keys_profile_id_channel_id",
                table: "stream_keys",
                columns: new[] { "profile_id", "channel_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AspNetRoleClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserLogins");

            migrationBuilder.DropTable(
                name: "AspNetUserPasskeys");

            migrationBuilder.DropTable(
                name: "AspNetUserRoles");

            migrationBuilder.DropTable(
                name: "AspNetUserTokens");

            migrationBuilder.DropTable(
                name: "channel_match_rules");

            migrationBuilder.DropTable(
                name: "channel_sources");

            migrationBuilder.DropTable(
                name: "epg_channel_map");

            migrationBuilder.DropTable(
                name: "profile_providers");

            migrationBuilder.DropTable(
                name: "site_settings");

            migrationBuilder.DropTable(
                name: "snapshots");

            migrationBuilder.DropTable(
                name: "stream_keys");

            migrationBuilder.DropTable(
                name: "AspNetRoles");

            migrationBuilder.DropTable(
                name: "AspNetUsers");

            migrationBuilder.DropTable(
                name: "provider_channels");

            migrationBuilder.DropTable(
                name: "canonical_channels");

            migrationBuilder.DropTable(
                name: "fetch_runs");

            migrationBuilder.DropTable(
                name: "provider_groups");

            migrationBuilder.DropTable(
                name: "profiles");

            migrationBuilder.DropTable(
                name: "providers");
        }
    }
}

