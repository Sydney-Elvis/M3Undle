using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M3Undle.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Alpha_Schema : Migration
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
                    AdaptiveLockoutEscalated = table.Column<bool>(type: "INTEGER", nullable: false),
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
                name: "metrics_tokens",
                columns: table => new
                {
                    metrics_token_id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    token_hash = table.Column<string>(type: "TEXT", nullable: false),
                    scope = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "metrics:read"),
                    created_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    last_used_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    expires_utc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_metrics_tokens", x => x.metrics_token_id);
                });

            migrationBuilder.CreateTable(
                name: "profiles",
                columns: table => new
                {
                    profile_id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    is_active = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    output_name = table.Column<string>(type: "TEXT", nullable: false),
                    merge_mode = table.Column<string>(type: "TEXT", nullable: false),
                    refresh_schedule_kind_override = table.Column<string>(type: "TEXT", nullable: true),
                    refresh_startup_catchup_override = table.Column<bool>(type: "INTEGER", nullable: true),
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
                    playlist_url = table.Column<string>(type: "TEXT", nullable: false),
                    xmltv_url = table.Column<string>(type: "TEXT", nullable: true),
                    headers_json = table.Column<string>(type: "TEXT", nullable: true),
                    user_agent = table.Column<string>(type: "TEXT", nullable: true),
                    timeout_seconds = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 20),
                    max_concurrent_streams = table.Column<int>(type: "INTEGER", nullable: true),
                    created_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    include_vod = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    include_series = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    force_mpegts = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    clean_relay_mode = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "auto"),
                    xtream_base_url = table.Column<string>(type: "TEXT", nullable: true),
                    xtream_username = table.Column<string>(type: "TEXT", nullable: true),
                    xtream_encrypted_password = table.Column<string>(type: "TEXT", nullable: true),
                    xtream_include_xmltv = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    xtream_detected_capable = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    playlist_expires_utc = table.Column<DateTime>(type: "TEXT", nullable: true)
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
                    authentication_enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    endpoint_security_enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    streaming_enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    stream_max_concurrent_sessions = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 50),
                    stream_idle_grace_seconds = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 15),
                    stream_idle_grace_hard_cap_seconds = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 120),
                    stream_buffer_max_bytes_per_session = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 4194304),
                    stream_buffer_max_bytes_hard_cap = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 33554432),
                    stream_buffer_read_chunk_size_bytes = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 32768),
                    stream_reconnect_read_stall_timeout_seconds = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 30),
                    stream_reconnect_outage_window_seconds = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 75),
                    stream_reconnect_connect_timeout_seconds = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 15),
                    streaming_settings_restart_required = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    hdhr_enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    hdhr_tuner_count_override = table.Column<int>(type: "INTEGER", nullable: true),
                    hdhr_advertised_base_url = table.Column<string>(type: "TEXT", nullable: true),
                    hdhr_discovery_enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    hdhr_ssdp_enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    hdhr_silicondust_discovery_enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    hdhr_friendly_name = table.Column<string>(type: "TEXT", nullable: true),
                    hdhr_settings_restart_required = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    generated_hls_enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    generated_hls_ffmpeg_path = table.Column<string>(type: "TEXT", nullable: true),
                    generated_hls_settings_restart_required = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    refresh_schedule_kind = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "6h"),
                    refresh_startup_catchup = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    event_retention_days = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 7),
                    observability_metrics_enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    observability_metrics_mode = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "LocalOnly"),
                    observability_metrics_enable_channel_labels = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    observability_metrics_local_allowed_cidrs = table.Column<string>(type: "TEXT", nullable: true),
                    xtream_compatibility_enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    hdhr_allowed_networks = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_site_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "stream_channel_health_events",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    provider_id = table.Column<string>(type: "TEXT", nullable: false),
                    provider_channel_id = table.Column<string>(type: "TEXT", nullable: false),
                    display_name = table.Column<string>(type: "TEXT", nullable: false),
                    event_kind = table.Column<string>(type: "TEXT", nullable: false),
                    event_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    session_id = table.Column<string>(type: "TEXT", nullable: true),
                    relay_mode = table.Column<string>(type: "TEXT", nullable: true),
                    route_classification = table.Column<string>(type: "TEXT", nullable: true),
                    upstream_failure_kind = table.Column<string>(type: "TEXT", nullable: true),
                    reconnect_attempt = table.Column<int>(type: "INTEGER", nullable: true),
                    stall_duration_ms = table.Column<double>(type: "REAL", nullable: true),
                    recovery_duration_ms = table.Column<double>(type: "REAL", nullable: true),
                    safe_start_wait_ms = table.Column<double>(type: "REAL", nullable: true),
                    output_held_ms = table.Column<double>(type: "REAL", nullable: true),
                    safe_start_kind = table.Column<string>(type: "TEXT", nullable: true),
                    client_disconnect_reason = table.Column<string>(type: "TEXT", nullable: true),
                    client_abort_after_recovery = table.Column<bool>(type: "INTEGER", nullable: false),
                    client_abort_after_recovery_delay_ms = table.Column<double>(type: "REAL", nullable: true),
                    forced_retune = table.Column<bool>(type: "INTEGER", nullable: false),
                    ts_sync_loss = table.Column<bool>(type: "INTEGER", nullable: false),
                    bytes_suppressed = table.Column<long>(type: "INTEGER", nullable: true),
                    clean_watch_duration_ms = table.Column<double>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stream_channel_health_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "system_events",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    event_type = table.Column<string>(type: "TEXT", nullable: false),
                    severity = table.Column<string>(type: "TEXT", nullable: false),
                    title = table.Column<string>(type: "TEXT", nullable: false),
                    detail = table.Column<string>(type: "TEXT", nullable: true),
                    provider_id = table.Column<string>(type: "TEXT", nullable: true),
                    integration_id = table.Column<string>(type: "TEXT", nullable: true),
                    occurred_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    occurrence_count = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_system_events", x => x.id);
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

            migrationBuilder.CreateTable(
                name: "profile_custom_groups",
                columns: table => new
                {
                    custom_group_id = table.Column<string>(type: "TEXT", nullable: false),
                    profile_id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    decision = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "include"),
                    channel_mode = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "select"),
                    tracking_policy = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "review"),
                    tracking_keywords = table.Column<string>(type: "TEXT", nullable: true),
                    auto_num_start = table.Column<int>(type: "INTEGER", nullable: true),
                    auto_num_end = table.Column<int>(type: "INTEGER", nullable: true),
                    track_new_channels = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    sort_override = table.Column<int>(type: "INTEGER", nullable: true),
                    created_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_profile_custom_groups", x => x.custom_group_id);
                    table.ForeignKey(
                        name: "FK_profile_custom_groups_profiles_profile_id",
                        column: x => x.profile_id,
                        principalTable: "profiles",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
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
                    live_channel_count = table.Column<int>(type: "INTEGER", nullable: false),
                    vod_channel_count = table.Column<int>(type: "INTEGER", nullable: false),
                    series_channel_count = table.Column<int>(type: "INTEGER", nullable: false),
                    error_summary = table.Column<string>(type: "TEXT", nullable: true),
                    change_class = table.Column<string>(type: "TEXT", nullable: true)
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
                name: "epg_sources",
                columns: table => new
                {
                    epg_source_id = table.Column<string>(type: "TEXT", nullable: false),
                    provider_id = table.Column<string>(type: "TEXT", nullable: true),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "xmltv_url"),
                    url_or_path = table.Column<string>(type: "TEXT", nullable: true),
                    priority = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 10),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    headers_json = table.Column<string>(type: "TEXT", nullable: true),
                    user_agent = table.Column<string>(type: "TEXT", nullable: true),
                    timeout_seconds = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 30),
                    etag = table.Column<string>(type: "TEXT", nullable: true),
                    last_modified_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    last_success_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    last_failure_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    refresh_interval_hours = table.Column<int>(type: "INTEGER", nullable: true),
                    created_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_epg_sources", x => x.epg_source_id);
                    table.ForeignKey(
                        name: "FK_epg_sources_providers_provider_id",
                        column: x => x.provider_id,
                        principalTable: "providers",
                        principalColumn: "provider_id",
                        onDelete: ReferentialAction.Cascade);
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
                    active = table.Column<bool>(type: "INTEGER", nullable: false),
                    channel_count = table.Column<int>(type: "INTEGER", nullable: true),
                    content_type = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "live")
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
                name: "epg_fetch_runs",
                columns: table => new
                {
                    epg_fetch_run_id = table.Column<string>(type: "TEXT", nullable: false),
                    epg_source_id = table.Column<string>(type: "TEXT", nullable: false),
                    started_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    finished_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    bytes = table.Column<int>(type: "INTEGER", nullable: true),
                    channel_count = table.Column<int>(type: "INTEGER", nullable: true),
                    programme_count = table.Column<int>(type: "INTEGER", nullable: true),
                    error_summary = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_epg_fetch_runs", x => x.epg_fetch_run_id);
                    table.ForeignKey(
                        name: "FK_epg_fetch_runs_epg_sources_epg_source_id",
                        column: x => x.epg_source_id,
                        principalTable: "epg_sources",
                        principalColumn: "epg_source_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "epg_source_channels",
                columns: table => new
                {
                    epg_source_channel_id = table.Column<string>(type: "TEXT", nullable: false),
                    epg_source_id = table.Column<string>(type: "TEXT", nullable: false),
                    xmltv_channel_id = table.Column<string>(type: "TEXT", nullable: false),
                    display_name = table.Column<string>(type: "TEXT", nullable: false),
                    icon_url = table.Column<string>(type: "TEXT", nullable: true),
                    last_seen_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_epg_source_channels", x => x.epg_source_channel_id);
                    table.ForeignKey(
                        name: "FK_epg_source_channels_epg_sources_epg_source_id",
                        column: x => x.epg_source_id,
                        principalTable: "epg_sources",
                        principalColumn: "epg_source_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "profile_custom_group_provider_links",
                columns: table => new
                {
                    link_id = table.Column<string>(type: "TEXT", nullable: false),
                    custom_group_id = table.Column<string>(type: "TEXT", nullable: false),
                    provider_group_id = table.Column<string>(type: "TEXT", nullable: false),
                    created_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_profile_custom_group_provider_links", x => x.link_id);
                    table.ForeignKey(
                        name: "FK_profile_custom_group_provider_links_profile_custom_groups_custom_group_id",
                        column: x => x.custom_group_id,
                        principalTable: "profile_custom_groups",
                        principalColumn: "custom_group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_profile_custom_group_provider_links_provider_groups_provider_group_id",
                        column: x => x.provider_group_id,
                        principalTable: "provider_groups",
                        principalColumn: "provider_group_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "profile_event_interest_rules",
                columns: table => new
                {
                    rule_id = table.Column<string>(type: "TEXT", nullable: false),
                    profile_id = table.Column<string>(type: "TEXT", nullable: false),
                    provider_id = table.Column<string>(type: "TEXT", nullable: true),
                    provider_group_id = table.Column<string>(type: "TEXT", nullable: true),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    match_type = table.Column<string>(type: "TEXT", nullable: false),
                    match_value = table.Column<string>(type: "TEXT", nullable: false),
                    action = table.Column<string>(type: "TEXT", nullable: false),
                    priority = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 100),
                    created_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_profile_event_interest_rules", x => x.rule_id);
                    table.ForeignKey(
                        name: "FK_profile_event_interest_rules_profiles_profile_id",
                        column: x => x.profile_id,
                        principalTable: "profiles",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_profile_event_interest_rules_provider_groups_provider_group_id",
                        column: x => x.provider_group_id,
                        principalTable: "provider_groups",
                        principalColumn: "provider_group_id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_profile_event_interest_rules_providers_provider_id",
                        column: x => x.provider_id,
                        principalTable: "providers",
                        principalColumn: "provider_id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "profile_group_filters",
                columns: table => new
                {
                    profile_group_filter_id = table.Column<string>(type: "TEXT", nullable: false),
                    profile_id = table.Column<string>(type: "TEXT", nullable: false),
                    provider_group_id = table.Column<string>(type: "TEXT", nullable: false),
                    decision = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "include"),
                    is_new = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    channel_mode = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "select"),
                    tracking_policy = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "review"),
                    tracking_keywords = table.Column<string>(type: "TEXT", nullable: true),
                    output_name = table.Column<string>(type: "TEXT", nullable: true),
                    auto_num_start = table.Column<int>(type: "INTEGER", nullable: true),
                    auto_num_end = table.Column<int>(type: "INTEGER", nullable: true),
                    track_new_channels = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    sort_override = table.Column<int>(type: "INTEGER", nullable: true),
                    created_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_profile_group_filters", x => x.profile_group_filter_id);
                    table.ForeignKey(
                        name: "FK_profile_group_filters_profiles_profile_id",
                        column: x => x.profile_id,
                        principalTable: "profiles",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_profile_group_filters_provider_groups_provider_group_id",
                        column: x => x.provider_group_id,
                        principalTable: "provider_groups",
                        principalColumn: "provider_group_id",
                        onDelete: ReferentialAction.Cascade);
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
                    is_placeholder = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    event_slot_key = table.Column<string>(type: "TEXT", nullable: true),
                    event_content_key = table.Column<string>(type: "TEXT", nullable: true),
                    event_title = table.Column<string>(type: "TEXT", nullable: true),
                    event_sport = table.Column<string>(type: "TEXT", nullable: true),
                    event_league = table.Column<string>(type: "TEXT", nullable: true),
                    event_participants_json = table.Column<string>(type: "TEXT", nullable: true),
                    event_start_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    event_end_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    first_seen_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    last_seen_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    active = table.Column<bool>(type: "INTEGER", nullable: false),
                    content_type = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "live"),
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

            migrationBuilder.CreateTable(
                name: "epg_channel_mappings",
                columns: table => new
                {
                    epg_channel_mapping_id = table.Column<string>(type: "TEXT", nullable: false),
                    profile_id = table.Column<string>(type: "TEXT", nullable: false),
                    provider_channel_id = table.Column<string>(type: "TEXT", nullable: false),
                    epg_source_id = table.Column<string>(type: "TEXT", nullable: false),
                    xmltv_channel_id = table.Column<string>(type: "TEXT", nullable: false),
                    mapping_mode = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "auto_id"),
                    confidence = table.Column<float>(type: "REAL", nullable: false, defaultValue: 1f),
                    created_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_epg_channel_mappings", x => x.epg_channel_mapping_id);
                    table.ForeignKey(
                        name: "FK_epg_channel_mappings_epg_sources_epg_source_id",
                        column: x => x.epg_source_id,
                        principalTable: "epg_sources",
                        principalColumn: "epg_source_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_epg_channel_mappings_profiles_profile_id",
                        column: x => x.profile_id,
                        principalTable: "profiles",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_epg_channel_mappings_provider_channels_provider_channel_id",
                        column: x => x.provider_channel_id,
                        principalTable: "provider_channels",
                        principalColumn: "provider_channel_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "profile_custom_group_channels",
                columns: table => new
                {
                    custom_group_channel_id = table.Column<string>(type: "TEXT", nullable: false),
                    custom_group_id = table.Column<string>(type: "TEXT", nullable: false),
                    provider_channel_id = table.Column<string>(type: "TEXT", nullable: false),
                    state = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "included"),
                    channel_number = table.Column<int>(type: "INTEGER", nullable: true),
                    display_name_override = table.Column<string>(type: "TEXT", nullable: true),
                    tvg_id_override = table.Column<string>(type: "TEXT", nullable: true),
                    created_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_profile_custom_group_channels", x => x.custom_group_channel_id);
                    table.ForeignKey(
                        name: "FK_profile_custom_group_channels_profile_custom_groups_custom_group_id",
                        column: x => x.custom_group_id,
                        principalTable: "profile_custom_groups",
                        principalColumn: "custom_group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_profile_custom_group_channels_provider_channels_provider_channel_id",
                        column: x => x.provider_channel_id,
                        principalTable: "provider_channels",
                        principalColumn: "provider_channel_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "profile_group_channel_filters",
                columns: table => new
                {
                    profile_group_channel_filter_id = table.Column<string>(type: "TEXT", nullable: false),
                    profile_group_filter_id = table.Column<string>(type: "TEXT", nullable: false),
                    provider_channel_id = table.Column<string>(type: "TEXT", nullable: false),
                    state = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "included"),
                    display_name_override = table.Column<string>(type: "TEXT", nullable: true),
                    output_group_name = table.Column<string>(type: "TEXT", nullable: true),
                    channel_number = table.Column<int>(type: "INTEGER", nullable: true),
                    tvg_id_override = table.Column<string>(type: "TEXT", nullable: true),
                    created_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_profile_group_channel_filters", x => x.profile_group_channel_filter_id);
                    table.ForeignKey(
                        name: "FK_profile_group_channel_filters_profile_group_filters_profile_group_filter_id",
                        column: x => x.profile_group_filter_id,
                        principalTable: "profile_group_filters",
                        principalColumn: "profile_group_filter_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_profile_group_channel_filters_provider_channels_provider_channel_id",
                        column: x => x.provider_channel_id,
                        principalTable: "provider_channels",
                        principalColumn: "provider_channel_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "site_settings",
                columns: new[] { "id", "event_retention_days", "generated_hls_enabled", "generated_hls_ffmpeg_path", "hdhr_advertised_base_url", "hdhr_allowed_networks", "hdhr_discovery_enabled", "hdhr_enabled", "hdhr_friendly_name", "hdhr_silicondust_discovery_enabled", "hdhr_ssdp_enabled", "hdhr_tuner_count_override", "observability_metrics_enabled", "observability_metrics_local_allowed_cidrs", "observability_metrics_mode", "refresh_schedule_kind", "refresh_startup_catchup", "stream_buffer_max_bytes_hard_cap", "stream_buffer_max_bytes_per_session", "stream_buffer_read_chunk_size_bytes", "stream_idle_grace_hard_cap_seconds", "stream_idle_grace_seconds", "stream_max_concurrent_sessions", "stream_reconnect_connect_timeout_seconds", "stream_reconnect_outage_window_seconds", "stream_reconnect_read_stall_timeout_seconds", "streaming_enabled", "xtream_compatibility_enabled" },
                values: new object[] { 1, 7, true, null, null, null, true, true, null, true, true, null, true, null, "LocalOnly", "6h", true, 33554432, 4194304, 32768, 120, 15, 50, 15, 75, 30, true, true });

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
                name: "idx_downstream_integrations_profile",
                table: "downstream_integrations",
                column: "profile_id");

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
                name: "idx_epg_channel_mappings_profile_channel_source",
                table: "epg_channel_mappings",
                columns: new[] { "profile_id", "provider_channel_id", "epg_source_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_epg_channel_mappings_epg_source_id",
                table: "epg_channel_mappings",
                column: "epg_source_id");

            migrationBuilder.CreateIndex(
                name: "IX_epg_channel_mappings_provider_channel_id",
                table: "epg_channel_mappings",
                column: "provider_channel_id");

            migrationBuilder.CreateIndex(
                name: "idx_epg_fetch_runs_source_time",
                table: "epg_fetch_runs",
                columns: new[] { "epg_source_id", "started_utc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "idx_epg_source_channels_source_channel",
                table: "epg_source_channels",
                columns: new[] { "epg_source_id", "xmltv_channel_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_epg_sources_provider",
                table: "epg_sources",
                column: "provider_id");

            migrationBuilder.CreateIndex(
                name: "idx_epg_sources_provider_priority",
                table: "epg_sources",
                columns: new[] { "provider_id", "priority" });

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
                name: "IX_metrics_tokens_name",
                table: "metrics_tokens",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_pcgc_group_channel_unique",
                table: "profile_custom_group_channels",
                columns: new[] { "custom_group_id", "provider_channel_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_pcgc_group_state",
                table: "profile_custom_group_channels",
                columns: new[] { "custom_group_id", "state" });

            migrationBuilder.CreateIndex(
                name: "IX_profile_custom_group_channels_provider_channel_id",
                table: "profile_custom_group_channels",
                column: "provider_channel_id");

            migrationBuilder.CreateIndex(
                name: "idx_pcgpl_group_provider_unique",
                table: "profile_custom_group_provider_links",
                columns: new[] { "custom_group_id", "provider_group_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_profile_custom_group_provider_links_provider_group_id",
                table: "profile_custom_group_provider_links",
                column: "provider_group_id");

            migrationBuilder.CreateIndex(
                name: "idx_pcg_profile_id",
                table: "profile_custom_groups",
                column: "profile_id");

            migrationBuilder.CreateIndex(
                name: "idx_pcg_profile_name_unique",
                table: "profile_custom_groups",
                columns: new[] { "profile_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_peir_profile_enabled_priority",
                table: "profile_event_interest_rules",
                columns: new[] { "profile_id", "enabled", "priority" });

            migrationBuilder.CreateIndex(
                name: "IX_profile_event_interest_rules_provider_group_id",
                table: "profile_event_interest_rules",
                column: "provider_group_id");

            migrationBuilder.CreateIndex(
                name: "IX_profile_event_interest_rules_provider_id",
                table: "profile_event_interest_rules",
                column: "provider_id");

            migrationBuilder.CreateIndex(
                name: "idx_pgcf_filter_channel_unique",
                table: "profile_group_channel_filters",
                columns: new[] { "profile_group_filter_id", "provider_channel_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_profile_group_channel_filters_provider_channel_id",
                table: "profile_group_channel_filters",
                column: "provider_channel_id");

            migrationBuilder.CreateIndex(
                name: "idx_pgf_profile_decision",
                table: "profile_group_filters",
                columns: new[] { "profile_id", "decision" });

            migrationBuilder.CreateIndex(
                name: "idx_pgf_profile_group_unique",
                table: "profile_group_filters",
                columns: new[] { "profile_id", "provider_group_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_pgf_profile_tracking_policy",
                table: "profile_group_filters",
                columns: new[] { "profile_id", "tracking_policy" });

            migrationBuilder.CreateIndex(
                name: "IX_profile_group_filters_provider_group_id",
                table: "profile_group_filters",
                column: "provider_group_id");

            migrationBuilder.CreateIndex(
                name: "idx_profile_providers_profile",
                table: "profile_providers",
                columns: new[] { "profile_id", "priority" });

            migrationBuilder.CreateIndex(
                name: "IX_profile_providers_provider_id",
                table: "profile_providers",
                column: "provider_id");

            migrationBuilder.CreateIndex(
                name: "idx_profiles_is_active",
                table: "profiles",
                column: "is_active",
                unique: true,
                filter: "is_active = 1");

            migrationBuilder.CreateIndex(
                name: "IX_profiles_name",
                table: "profiles",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_provider_channels_event_content",
                table: "provider_channels",
                columns: new[] { "provider_id", "event_content_key" },
                filter: "event_content_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "idx_provider_channels_is_event",
                table: "provider_channels",
                columns: new[] { "provider_id", "is_event", "event_start_utc" });

            migrationBuilder.CreateIndex(
                name: "idx_provider_channels_placeholder_active",
                table: "provider_channels",
                columns: new[] { "provider_id", "is_placeholder", "active" });

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
                name: "ix_stream_channel_health_events_event_kind_event_utc",
                table: "stream_channel_health_events",
                columns: new[] { "event_kind", "event_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_stream_channel_health_events_provider_channel_event_utc",
                table: "stream_channel_health_events",
                columns: new[] { "provider_id", "provider_channel_id", "event_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_stream_channel_health_events_session_id",
                table: "stream_channel_health_events",
                column: "session_id");

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

            migrationBuilder.CreateIndex(
                name: "ix_system_events_event_type_integration_id",
                table: "system_events",
                columns: new[] { "event_type", "integration_id" },
                filter: "\"integration_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_system_events_event_type_provider_id",
                table: "system_events",
                columns: new[] { "event_type", "provider_id" },
                filter: "\"provider_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_system_events_occurred_at",
                table: "system_events",
                column: "occurred_at");
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
                name: "downstream_integrations");

            migrationBuilder.DropTable(
                name: "endpoint_access_bindings");

            migrationBuilder.DropTable(
                name: "epg_channel_map");

            migrationBuilder.DropTable(
                name: "epg_channel_mappings");

            migrationBuilder.DropTable(
                name: "epg_fetch_runs");

            migrationBuilder.DropTable(
                name: "epg_source_channels");

            migrationBuilder.DropTable(
                name: "metrics_tokens");

            migrationBuilder.DropTable(
                name: "profile_custom_group_channels");

            migrationBuilder.DropTable(
                name: "profile_custom_group_provider_links");

            migrationBuilder.DropTable(
                name: "profile_event_interest_rules");

            migrationBuilder.DropTable(
                name: "profile_group_channel_filters");

            migrationBuilder.DropTable(
                name: "profile_providers");

            migrationBuilder.DropTable(
                name: "site_settings");

            migrationBuilder.DropTable(
                name: "snapshots");

            migrationBuilder.DropTable(
                name: "stream_channel_health_events");

            migrationBuilder.DropTable(
                name: "stream_keys");

            migrationBuilder.DropTable(
                name: "system_events");

            migrationBuilder.DropTable(
                name: "AspNetRoles");

            migrationBuilder.DropTable(
                name: "AspNetUsers");

            migrationBuilder.DropTable(
                name: "endpoint_credentials");

            migrationBuilder.DropTable(
                name: "epg_sources");

            migrationBuilder.DropTable(
                name: "profile_custom_groups");

            migrationBuilder.DropTable(
                name: "profile_group_filters");

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
