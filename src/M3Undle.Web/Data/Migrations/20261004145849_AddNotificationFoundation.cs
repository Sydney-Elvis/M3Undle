using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M3Undle.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "epg_notification_coverage",
                columns: table => new
                {
                    epg_notification_coverage_id = table.Column<string>(type: "TEXT", nullable: false),
                    epg_source_id = table.Column<string>(type: "TEXT", nullable: false),
                    xmltv_channel_id = table.Column<string>(type: "TEXT", nullable: false),
                    is_relevant = table.Column<bool>(type: "INTEGER", nullable: false),
                    relevance_context = table.Column<string>(type: "TEXT", nullable: true),
                    intervals_encoded = table.Column<string>(type: "TEXT", nullable: false),
                    interval_count = table.Column<int>(type: "INTEGER", nullable: false),
                    evidence_revision = table.Column<string>(type: "TEXT", nullable: true),
                    updated_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_epg_notification_coverage", x => x.epg_notification_coverage_id);
                    table.ForeignKey(
                        name: "FK_epg_notification_coverage_epg_sources_epg_source_id",
                        column: x => x.epg_source_id,
                        principalTable: "epg_sources",
                        principalColumn: "epg_source_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notification_condition_observations",
                columns: table => new
                {
                    observation_id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    evidence_key = table.Column<string>(type: "TEXT", nullable: false),
                    subject_kind = table.Column<string>(type: "TEXT", nullable: false),
                    subject_id = table.Column<string>(type: "TEXT", nullable: false),
                    completed_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    outcome = table.Column<string>(type: "TEXT", nullable: false),
                    safe_detail = table.Column<string>(type: "TEXT", nullable: true),
                    consumed = table.Column<bool>(type: "INTEGER", nullable: false),
                    consumed_utc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_condition_observations", x => x.observation_id);
                });

            migrationBuilder.CreateTable(
                name: "notification_destinations",
                columns: table => new
                {
                    destination_id = table.Column<string>(type: "TEXT", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    config_revision = table.Column<int>(type: "INTEGER", nullable: false),
                    delivery_identity_revision = table.Column<int>(type: "INTEGER", nullable: false),
                    verified_revision = table.Column<int>(type: "INTEGER", nullable: true),
                    verified_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    verification_status = table.Column<string>(type: "TEXT", nullable: false),
                    verification_detail = table.Column<string>(type: "TEXT", nullable: true),
                    created_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_destinations", x => x.destination_id);
                });

            migrationBuilder.CreateTable(
                name: "notification_incidents",
                columns: table => new
                {
                    incident_id = table.Column<string>(type: "TEXT", nullable: false),
                    notification_key = table.Column<string>(type: "TEXT", nullable: false),
                    subject_kind = table.Column<string>(type: "TEXT", nullable: false),
                    subject_id = table.Column<string>(type: "TEXT", nullable: false),
                    subject_label = table.Column<string>(type: "TEXT", nullable: true),
                    generation = table.Column<int>(type: "INTEGER", nullable: false),
                    state = table.Column<string>(type: "TEXT", nullable: false),
                    severity = table.Column<string>(type: "TEXT", nullable: false),
                    first_unhealthy_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    last_observed_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    resolved_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    reason = table.Column<string>(type: "TEXT", nullable: true),
                    safe_detail = table.Column<string>(type: "TEXT", nullable: true),
                    consecutive_healthy = table.Column<int>(type: "INTEGER", nullable: false),
                    opened_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    last_reminder_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    reminder_sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    updated_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_incidents", x => x.incident_id);
                });

            migrationBuilder.CreateTable(
                name: "notification_occurrences",
                columns: table => new
                {
                    occurrence_id = table.Column<string>(type: "TEXT", nullable: false),
                    occurrence_key = table.Column<string>(type: "TEXT", nullable: false),
                    notification_key = table.Column<string>(type: "TEXT", nullable: false),
                    incident_id = table.Column<string>(type: "TEXT", nullable: true),
                    generation = table.Column<int>(type: "INTEGER", nullable: true),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    policy_revision = table.Column<int>(type: "INTEGER", nullable: false),
                    activation_epoch = table.Column<int>(type: "INTEGER", nullable: false),
                    severity = table.Column<string>(type: "TEXT", nullable: false),
                    title = table.Column<string>(type: "TEXT", nullable: false),
                    body = table.Column<string>(type: "TEXT", nullable: false),
                    subject_label = table.Column<string>(type: "TEXT", nullable: true),
                    link_path = table.Column<string>(type: "TEXT", nullable: true),
                    occurred_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    created_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    materialized_utc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_occurrences", x => x.occurrence_id);
                });

            migrationBuilder.CreateTable(
                name: "notification_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false),
                    sending_enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    paused = table.Column<bool>(type: "INTEGER", nullable: false),
                    requires_activation = table.Column<bool>(type: "INTEGER", nullable: false),
                    activation_epoch = table.Column<int>(type: "INTEGER", nullable: false),
                    failure_delay_minutes = table.Column<int>(type: "INTEGER", nullable: false),
                    overdue_grace_minutes = table.Column<int>(type: "INTEGER", nullable: false),
                    reminder_interval_hours = table.Column<int>(type: "INTEGER", nullable: false),
                    coverage_warn_hours = table.Column<int>(type: "INTEGER", nullable: false),
                    coverage_warn_percent = table.Column<int>(type: "INTEGER", nullable: false),
                    coverage_recover_hours = table.Column<int>(type: "INTEGER", nullable: false),
                    coverage_recover_percent = table.Column<int>(type: "INTEGER", nullable: false),
                    coverage_gap_minutes = table.Column<int>(type: "INTEGER", nullable: false),
                    retention_days = table.Column<int>(type: "INTEGER", nullable: false),
                    identifier_salt = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    revision = table.Column<int>(type: "INTEGER", nullable: false),
                    capacity_suppressed_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    updated_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notification_email_recipients",
                columns: table => new
                {
                    recipient_id = table.Column<string>(type: "TEXT", nullable: false),
                    destination_id = table.Column<string>(type: "TEXT", nullable: false),
                    address = table.Column<string>(type: "TEXT", nullable: false),
                    canonical_key = table.Column<string>(type: "TEXT", nullable: false),
                    sort_order = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_email_recipients", x => x.recipient_id);
                    table.ForeignKey(
                        name: "FK_notification_email_recipients_notification_destinations_destination_id",
                        column: x => x.destination_id,
                        principalTable: "notification_destinations",
                        principalColumn: "destination_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notification_matrix_settings",
                columns: table => new
                {
                    destination_id = table.Column<string>(type: "TEXT", nullable: false),
                    homeserver_url = table.Column<string>(type: "TEXT", nullable: true),
                    room_id = table.Column<string>(type: "TEXT", nullable: true),
                    access_token_encrypted = table.Column<string>(type: "TEXT", nullable: true),
                    bot_user_id = table.Column<string>(type: "TEXT", nullable: true),
                    device_id = table.Column<string>(type: "TEXT", nullable: true),
                    allow_insecure_http = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_matrix_settings", x => x.destination_id);
                    table.ForeignKey(
                        name: "FK_notification_matrix_settings_notification_destinations_destination_id",
                        column: x => x.destination_id,
                        principalTable: "notification_destinations",
                        principalColumn: "destination_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notification_routes",
                columns: table => new
                {
                    notification_key = table.Column<string>(type: "TEXT", nullable: false),
                    destination_id = table.Column<string>(type: "TEXT", nullable: true),
                    revision = table.Column<int>(type: "INTEGER", nullable: false),
                    send_recovery = table.Column<bool>(type: "INTEGER", nullable: false),
                    send_reminders = table.Column<bool>(type: "INTEGER", nullable: false),
                    failure_delay_minutes = table.Column<int>(type: "INTEGER", nullable: true),
                    reminder_interval_hours = table.Column<int>(type: "INTEGER", nullable: true),
                    updated_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_routes", x => x.notification_key);
                    table.ForeignKey(
                        name: "FK_notification_routes_notification_destinations_destination_id",
                        column: x => x.destination_id,
                        principalTable: "notification_destinations",
                        principalColumn: "destination_id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "notification_smtp_settings",
                columns: table => new
                {
                    destination_id = table.Column<string>(type: "TEXT", nullable: false),
                    host = table.Column<string>(type: "TEXT", nullable: true),
                    port = table.Column<int>(type: "INTEGER", nullable: false),
                    tls_mode = table.Column<string>(type: "TEXT", nullable: false),
                    auth_mode = table.Column<string>(type: "TEXT", nullable: false),
                    username = table.Column<string>(type: "TEXT", nullable: true),
                    password_encrypted = table.Column<string>(type: "TEXT", nullable: true),
                    sender_address = table.Column<string>(type: "TEXT", nullable: true),
                    sender_name = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_smtp_settings", x => x.destination_id);
                    table.ForeignKey(
                        name: "FK_notification_smtp_settings_notification_destinations_destination_id",
                        column: x => x.destination_id,
                        principalTable: "notification_destinations",
                        principalColumn: "destination_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notification_incident_targets",
                columns: table => new
                {
                    incident_target_id = table.Column<string>(type: "TEXT", nullable: false),
                    incident_id = table.Column<string>(type: "TEXT", nullable: false),
                    generation = table.Column<int>(type: "INTEGER", nullable: false),
                    destination_id = table.Column<string>(type: "TEXT", nullable: false),
                    target_id = table.Column<string>(type: "TEXT", nullable: false),
                    delivery_identity_revision = table.Column<int>(type: "INTEGER", nullable: false),
                    opening_accepted_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    opening_uncertain = table.Column<bool>(type: "INTEGER", nullable: false),
                    recovery_queued_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    recovery_accepted_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    last_reminder_sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    updated_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_incident_targets", x => x.incident_target_id);
                    table.ForeignKey(
                        name: "FK_notification_incident_targets_notification_incidents_incident_id",
                        column: x => x.incident_id,
                        principalTable: "notification_incidents",
                        principalColumn: "incident_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "notification_deliveries",
                columns: table => new
                {
                    delivery_id = table.Column<string>(type: "TEXT", nullable: false),
                    occurrence_id = table.Column<string>(type: "TEXT", nullable: false),
                    destination_id = table.Column<string>(type: "TEXT", nullable: false),
                    provider_kind = table.Column<string>(type: "TEXT", nullable: false),
                    target_id = table.Column<string>(type: "TEXT", nullable: false),
                    target_label = table.Column<string>(type: "TEXT", nullable: false),
                    delivery_identity_revision = table.Column<int>(type: "INTEGER", nullable: false),
                    config_revision = table.Column<int>(type: "INTEGER", nullable: false),
                    route_revision = table.Column<int>(type: "INTEGER", nullable: false),
                    state = table.Column<string>(type: "TEXT", nullable: false),
                    attempt_count = table.Column<int>(type: "INTEGER", nullable: false),
                    cycle_number = table.Column<int>(type: "INTEGER", nullable: false),
                    due_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    claim_owner = table.Column<string>(type: "TEXT", nullable: true),
                    claim_expires_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    transport_started_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    accepted_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    remote_reference = table.Column<string>(type: "TEXT", nullable: true),
                    error_code = table.Column<string>(type: "TEXT", nullable: true),
                    error_text = table.Column<string>(type: "TEXT", nullable: true),
                    suppressed_reason = table.Column<string>(type: "TEXT", nullable: true),
                    payload_title = table.Column<string>(type: "TEXT", nullable: false),
                    payload_body = table.Column<string>(type: "TEXT", nullable: false),
                    payload_link_path = table.Column<string>(type: "TEXT", nullable: true),
                    message_id = table.Column<string>(type: "TEXT", nullable: false),
                    created_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    dismissed_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    revision = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_deliveries", x => x.delivery_id);
                    table.ForeignKey(
                        name: "FK_notification_deliveries_notification_destinations_destination_id",
                        column: x => x.destination_id,
                        principalTable: "notification_destinations",
                        principalColumn: "destination_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_notification_deliveries_notification_occurrences_occurrence_id",
                        column: x => x.occurrence_id,
                        principalTable: "notification_occurrences",
                        principalColumn: "occurrence_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_epg_notification_coverage_channel",
                table: "epg_notification_coverage",
                columns: new[] { "epg_source_id", "xmltv_channel_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_notification_observations_subject",
                table: "notification_condition_observations",
                columns: new[] { "evidence_key", "subject_id", "observation_id" });

            migrationBuilder.CreateIndex(
                name: "ix_notification_observations_unconsumed",
                table: "notification_condition_observations",
                columns: new[] { "consumed", "observation_id" });

            migrationBuilder.CreateIndex(
                name: "IX_notification_deliveries_destination_id",
                table: "notification_deliveries",
                column: "destination_id");

            migrationBuilder.CreateIndex(
                name: "ix_notification_deliveries_due",
                table: "notification_deliveries",
                columns: new[] { "state", "provider_kind", "due_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_notification_deliveries_identity",
                table: "notification_deliveries",
                columns: new[] { "occurrence_id", "destination_id", "target_id", "delivery_identity_revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_notification_destinations_kind",
                table: "notification_destinations",
                column: "kind",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_notification_email_recipients_identity",
                table: "notification_email_recipients",
                columns: new[] { "destination_id", "canonical_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_notification_incident_targets_identity",
                table: "notification_incident_targets",
                columns: new[] { "incident_id", "generation", "destination_id", "target_id", "delivery_identity_revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_notification_incidents_generation",
                table: "notification_incidents",
                columns: new[] { "notification_key", "subject_id", "generation" });

            migrationBuilder.CreateIndex(
                name: "ux_notification_incidents_active",
                table: "notification_incidents",
                columns: new[] { "notification_key", "subject_id" },
                unique: true,
                filter: "\"state\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "ix_notification_occurrences_created",
                table: "notification_occurrences",
                column: "created_utc");

            migrationBuilder.CreateIndex(
                name: "ix_notification_occurrences_incident",
                table: "notification_occurrences",
                column: "incident_id");

            migrationBuilder.CreateIndex(
                name: "ix_notification_occurrences_materialized",
                table: "notification_occurrences",
                column: "materialized_utc");

            migrationBuilder.CreateIndex(
                name: "ux_notification_occurrences_key",
                table: "notification_occurrences",
                column: "occurrence_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_notification_routes_destination_id",
                table: "notification_routes",
                column: "destination_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "epg_notification_coverage");

            migrationBuilder.DropTable(
                name: "notification_condition_observations");

            migrationBuilder.DropTable(
                name: "notification_deliveries");

            migrationBuilder.DropTable(
                name: "notification_email_recipients");

            migrationBuilder.DropTable(
                name: "notification_incident_targets");

            migrationBuilder.DropTable(
                name: "notification_matrix_settings");

            migrationBuilder.DropTable(
                name: "notification_routes");

            migrationBuilder.DropTable(
                name: "notification_settings");

            migrationBuilder.DropTable(
                name: "notification_smtp_settings");

            migrationBuilder.DropTable(
                name: "notification_occurrences");

            migrationBuilder.DropTable(
                name: "notification_incidents");

            migrationBuilder.DropTable(
                name: "notification_destinations");
        }
    }
}
