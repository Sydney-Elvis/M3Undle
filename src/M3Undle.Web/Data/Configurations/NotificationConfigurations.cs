using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace M3Undle.Web.Data.Configurations;

public sealed class NotificationSettingsConfiguration : IEntityTypeConfiguration<NotificationSettings>
{
    public void Configure(EntityTypeBuilder<NotificationSettings> builder)
    {
        builder.ToTable("notification_settings");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.SendingEnabled).HasColumnName("sending_enabled").IsRequired();
        builder.Property(x => x.Paused).HasColumnName("paused").IsRequired();
        builder.Property(x => x.RequiresActivation).HasColumnName("requires_activation").IsRequired();
        builder.Property(x => x.ActivationEpoch).HasColumnName("activation_epoch").IsRequired();
        builder.Property(x => x.FailureDelayMinutes).HasColumnName("failure_delay_minutes").IsRequired();
        builder.Property(x => x.OverdueGraceMinutes).HasColumnName("overdue_grace_minutes").IsRequired();
        builder.Property(x => x.ReminderIntervalHours).HasColumnName("reminder_interval_hours").IsRequired();
        builder.Property(x => x.CoverageWarnHours).HasColumnName("coverage_warn_hours").IsRequired();
        builder.Property(x => x.CoverageWarnPercent).HasColumnName("coverage_warn_percent").IsRequired();
        builder.Property(x => x.CoverageRecoverHours).HasColumnName("coverage_recover_hours").IsRequired();
        builder.Property(x => x.CoverageRecoverPercent).HasColumnName("coverage_recover_percent").IsRequired();
        builder.Property(x => x.CoverageGapMinutes).HasColumnName("coverage_gap_minutes").IsRequired();
        builder.Property(x => x.RetentionDays).HasColumnName("retention_days").IsRequired();
        builder.Property(x => x.IdentifierSalt).HasColumnName("identifier_salt").IsRequired().HasDefaultValue(string.Empty);
        builder.Property(x => x.Revision).HasColumnName("revision").IsRequired().IsConcurrencyToken();
        builder.Property(x => x.CapacitySuppressedUtc).HasColumnName("capacity_suppressed_utc");
        builder.Property(x => x.UpdatedUtc).HasColumnName("updated_utc").IsRequired();
    }
}

public sealed class NotificationDestinationConfiguration : IEntityTypeConfiguration<NotificationDestination>
{
    public void Configure(EntityTypeBuilder<NotificationDestination> builder)
    {
        builder.ToTable("notification_destinations");
        builder.HasKey(x => x.DestinationId);
        builder.Property(x => x.DestinationId).HasColumnName("destination_id");
        builder.Property(x => x.Kind).HasColumnName("kind").IsRequired();
        builder.Property(x => x.Enabled).HasColumnName("enabled").IsRequired();
        builder.Property(x => x.ConfigRevision).HasColumnName("config_revision").IsRequired().IsConcurrencyToken();
        builder.Property(x => x.DeliveryIdentityRevision).HasColumnName("delivery_identity_revision").IsRequired();
        builder.Property(x => x.VerifiedRevision).HasColumnName("verified_revision");
        builder.Property(x => x.VerifiedUtc).HasColumnName("verified_utc");
        builder.Property(x => x.VerificationStatus).HasColumnName("verification_status").IsRequired();
        builder.Property(x => x.VerificationDetail).HasColumnName("verification_detail");
        builder.Property(x => x.CreatedUtc).HasColumnName("created_utc").IsRequired();
        builder.Property(x => x.UpdatedUtc).HasColumnName("updated_utc").IsRequired();

        builder.HasIndex(x => x.Kind).IsUnique().HasDatabaseName("ux_notification_destinations_kind");

        builder.HasOne(x => x.Matrix).WithOne(x => x.Destination)
            .HasForeignKey<NotificationMatrixSettings>(x => x.DestinationId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.Smtp).WithOne(x => x.Destination)
            .HasForeignKey<NotificationSmtpSettings>(x => x.DestinationId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.Recipients).WithOne(x => x.Destination)
            .HasForeignKey(x => x.DestinationId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class NotificationMatrixSettingsConfiguration : IEntityTypeConfiguration<NotificationMatrixSettings>
{
    public void Configure(EntityTypeBuilder<NotificationMatrixSettings> builder)
    {
        builder.ToTable("notification_matrix_settings");
        builder.HasKey(x => x.DestinationId);
        builder.Property(x => x.DestinationId).HasColumnName("destination_id");
        builder.Property(x => x.HomeserverUrl).HasColumnName("homeserver_url");
        builder.Property(x => x.RoomId).HasColumnName("room_id");
        builder.Property(x => x.AccessTokenEncrypted).HasColumnName("access_token_encrypted");
        builder.Property(x => x.BotUserId).HasColumnName("bot_user_id");
        builder.Property(x => x.DeviceId).HasColumnName("device_id");
        builder.Property(x => x.AllowInsecureHttp).HasColumnName("allow_insecure_http").IsRequired();
    }
}

public sealed class NotificationSmtpSettingsConfiguration : IEntityTypeConfiguration<NotificationSmtpSettings>
{
    public void Configure(EntityTypeBuilder<NotificationSmtpSettings> builder)
    {
        builder.ToTable("notification_smtp_settings");
        builder.HasKey(x => x.DestinationId);
        builder.Property(x => x.DestinationId).HasColumnName("destination_id");
        builder.Property(x => x.Host).HasColumnName("host");
        builder.Property(x => x.Port).HasColumnName("port").IsRequired();
        builder.Property(x => x.TlsMode).HasColumnName("tls_mode").IsRequired();
        builder.Property(x => x.AuthMode).HasColumnName("auth_mode").IsRequired();
        builder.Property(x => x.Username).HasColumnName("username");
        builder.Property(x => x.PasswordEncrypted).HasColumnName("password_encrypted");
        builder.Property(x => x.SenderAddress).HasColumnName("sender_address");
        builder.Property(x => x.SenderName).HasColumnName("sender_name");
    }
}

public sealed class NotificationEmailRecipientConfiguration : IEntityTypeConfiguration<NotificationEmailRecipient>
{
    public void Configure(EntityTypeBuilder<NotificationEmailRecipient> builder)
    {
        builder.ToTable("notification_email_recipients");
        builder.HasKey(x => x.RecipientId);
        builder.Property(x => x.RecipientId).HasColumnName("recipient_id");
        builder.Property(x => x.DestinationId).HasColumnName("destination_id").IsRequired();
        builder.Property(x => x.Address).HasColumnName("address").IsRequired();
        builder.Property(x => x.CanonicalKey).HasColumnName("canonical_key").IsRequired();
        builder.Property(x => x.SortOrder).HasColumnName("sort_order").IsRequired();

        builder.HasIndex(x => new { x.DestinationId, x.CanonicalKey }).IsUnique()
            .HasDatabaseName("ux_notification_email_recipients_identity");
    }
}

public sealed class NotificationRouteConfiguration : IEntityTypeConfiguration<NotificationRoute>
{
    public void Configure(EntityTypeBuilder<NotificationRoute> builder)
    {
        builder.ToTable("notification_routes");
        builder.HasKey(x => x.NotificationKey);
        builder.Property(x => x.NotificationKey).HasColumnName("notification_key");
        builder.Property(x => x.DestinationId).HasColumnName("destination_id");
        builder.Property(x => x.Revision).HasColumnName("revision").IsRequired().IsConcurrencyToken();
        builder.Property(x => x.SendRecovery).HasColumnName("send_recovery").IsRequired();
        builder.Property(x => x.SendReminders).HasColumnName("send_reminders").IsRequired();
        builder.Property(x => x.FailureDelayMinutes).HasColumnName("failure_delay_minutes");
        builder.Property(x => x.ReminderIntervalHours).HasColumnName("reminder_interval_hours");
        builder.Property(x => x.UpdatedUtc).HasColumnName("updated_utc").IsRequired();

        builder.HasOne(x => x.Destination).WithMany()
            .HasForeignKey(x => x.DestinationId).OnDelete(DeleteBehavior.SetNull);
    }
}

public sealed class NotificationConditionObservationConfiguration : IEntityTypeConfiguration<NotificationConditionObservation>
{
    public void Configure(EntityTypeBuilder<NotificationConditionObservation> builder)
    {
        builder.ToTable("notification_condition_observations");
        builder.HasKey(x => x.ObservationId);
        builder.Property(x => x.ObservationId).HasColumnName("observation_id").ValueGeneratedOnAdd();
        builder.Property(x => x.EvidenceKey).HasColumnName("evidence_key").IsRequired();
        builder.Property(x => x.SubjectKind).HasColumnName("subject_kind").IsRequired();
        builder.Property(x => x.SubjectId).HasColumnName("subject_id").IsRequired();
        builder.Property(x => x.CompletedUtc).HasColumnName("completed_utc").IsRequired();
        builder.Property(x => x.Outcome).HasColumnName("outcome").IsRequired();
        builder.Property(x => x.SafeDetail).HasColumnName("safe_detail");
        builder.Property(x => x.Consumed).HasColumnName("consumed").IsRequired();
        builder.Property(x => x.ConsumedUtc).HasColumnName("consumed_utc");

        builder.HasIndex(x => new { x.Consumed, x.ObservationId }).HasDatabaseName("ix_notification_observations_unconsumed");
        builder.HasIndex(x => new { x.EvidenceKey, x.SubjectId, x.ObservationId }).HasDatabaseName("ix_notification_observations_subject");
    }
}

public sealed class NotificationIncidentConfiguration : IEntityTypeConfiguration<NotificationIncident>
{
    public void Configure(EntityTypeBuilder<NotificationIncident> builder)
    {
        builder.ToTable("notification_incidents");
        builder.HasKey(x => x.IncidentId);
        builder.Property(x => x.IncidentId).HasColumnName("incident_id");
        builder.Property(x => x.NotificationKey).HasColumnName("notification_key").IsRequired();
        builder.Property(x => x.SubjectKind).HasColumnName("subject_kind").IsRequired();
        builder.Property(x => x.SubjectId).HasColumnName("subject_id").IsRequired();
        builder.Property(x => x.SubjectLabel).HasColumnName("subject_label");
        builder.Property(x => x.Generation).HasColumnName("generation").IsRequired();
        builder.Property(x => x.State).HasColumnName("state").IsRequired();
        builder.Property(x => x.Severity).HasColumnName("severity").IsRequired();
        builder.Property(x => x.FirstUnhealthyUtc).HasColumnName("first_unhealthy_utc").IsRequired();
        builder.Property(x => x.LastObservedUtc).HasColumnName("last_observed_utc").IsRequired();
        builder.Property(x => x.ResolvedUtc).HasColumnName("resolved_utc");
        builder.Property(x => x.Reason).HasColumnName("reason");
        builder.Property(x => x.SafeDetail).HasColumnName("safe_detail");
        builder.Property(x => x.ConsecutiveHealthy).HasColumnName("consecutive_healthy").IsRequired();
        builder.Property(x => x.OpenedUtc).HasColumnName("opened_utc");
        builder.Property(x => x.LastReminderUtc).HasColumnName("last_reminder_utc");
        builder.Property(x => x.ReminderSequence).HasColumnName("reminder_sequence").IsRequired();
        builder.Property(x => x.UpdatedUtc).HasColumnName("updated_utc").IsRequired();

        builder.HasIndex(x => new { x.NotificationKey, x.SubjectId })
            .IsUnique()
            .HasFilter("\"state\" = 'Active'")
            .HasDatabaseName("ux_notification_incidents_active");
        builder.HasIndex(x => new { x.NotificationKey, x.SubjectId, x.Generation })
            .HasDatabaseName("ix_notification_incidents_generation");
    }
}

public sealed class NotificationOccurrenceConfiguration : IEntityTypeConfiguration<NotificationOccurrence>
{
    public void Configure(EntityTypeBuilder<NotificationOccurrence> builder)
    {
        builder.ToTable("notification_occurrences");
        builder.HasKey(x => x.OccurrenceId);
        builder.Property(x => x.OccurrenceId).HasColumnName("occurrence_id");
        builder.Property(x => x.OccurrenceKey).HasColumnName("occurrence_key").IsRequired();
        builder.Property(x => x.NotificationKey).HasColumnName("notification_key").IsRequired();
        builder.Property(x => x.IncidentId).HasColumnName("incident_id");
        builder.Property(x => x.Generation).HasColumnName("generation");
        builder.Property(x => x.Kind).HasColumnName("kind").IsRequired();
        builder.Property(x => x.Sequence).HasColumnName("sequence").IsRequired();
        builder.Property(x => x.PolicyRevision).HasColumnName("policy_revision").IsRequired();
        builder.Property(x => x.ActivationEpoch).HasColumnName("activation_epoch").IsRequired();
        builder.Property(x => x.Severity).HasColumnName("severity").IsRequired();
        builder.Property(x => x.Title).HasColumnName("title").IsRequired();
        builder.Property(x => x.Body).HasColumnName("body").IsRequired();
        builder.Property(x => x.SubjectLabel).HasColumnName("subject_label");
        builder.Property(x => x.LinkPath).HasColumnName("link_path");
        builder.Property(x => x.OccurredUtc).HasColumnName("occurred_utc").IsRequired();
        builder.Property(x => x.CreatedUtc).HasColumnName("created_utc").IsRequired();
        builder.Property(x => x.MaterializedUtc).HasColumnName("materialized_utc");

        builder.HasIndex(x => x.OccurrenceKey).IsUnique().HasDatabaseName("ux_notification_occurrences_key");
        builder.HasIndex(x => x.IncidentId).HasDatabaseName("ix_notification_occurrences_incident");
        builder.HasIndex(x => x.CreatedUtc).HasDatabaseName("ix_notification_occurrences_created");
        builder.HasIndex(x => x.MaterializedUtc).HasDatabaseName("ix_notification_occurrences_materialized");
    }
}

public sealed class NotificationIncidentTargetConfiguration : IEntityTypeConfiguration<NotificationIncidentTarget>
{
    public void Configure(EntityTypeBuilder<NotificationIncidentTarget> builder)
    {
        builder.ToTable("notification_incident_targets");
        builder.HasKey(x => x.IncidentTargetId);
        builder.Property(x => x.IncidentTargetId).HasColumnName("incident_target_id");
        builder.Property(x => x.IncidentId).HasColumnName("incident_id").IsRequired();
        builder.Property(x => x.Generation).HasColumnName("generation").IsRequired();
        builder.Property(x => x.DestinationId).HasColumnName("destination_id").IsRequired();
        builder.Property(x => x.TargetId).HasColumnName("target_id").IsRequired();
        builder.Property(x => x.DeliveryIdentityRevision).HasColumnName("delivery_identity_revision").IsRequired();
        builder.Property(x => x.OpeningAcceptedUtc).HasColumnName("opening_accepted_utc");
        builder.Property(x => x.OpeningUncertain).HasColumnName("opening_uncertain").IsRequired();
        builder.Property(x => x.RecoveryQueuedUtc).HasColumnName("recovery_queued_utc");
        builder.Property(x => x.RecoveryAcceptedUtc).HasColumnName("recovery_accepted_utc");
        builder.Property(x => x.LastReminderSequence).HasColumnName("last_reminder_sequence").IsRequired();
        builder.Property(x => x.UpdatedUtc).HasColumnName("updated_utc").IsRequired();

        builder.HasIndex(x => new { x.IncidentId, x.Generation, x.DestinationId, x.TargetId, x.DeliveryIdentityRevision })
            .IsUnique().HasDatabaseName("ux_notification_incident_targets_identity");

        builder.HasOne(x => x.Incident).WithMany()
            .HasForeignKey(x => x.IncidentId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class NotificationDeliveryConfiguration : IEntityTypeConfiguration<NotificationDelivery>
{
    public void Configure(EntityTypeBuilder<NotificationDelivery> builder)
    {
        builder.ToTable("notification_deliveries");
        builder.HasKey(x => x.DeliveryId);
        builder.Property(x => x.DeliveryId).HasColumnName("delivery_id");
        builder.Property(x => x.OccurrenceId).HasColumnName("occurrence_id").IsRequired();
        builder.Property(x => x.DestinationId).HasColumnName("destination_id").IsRequired();
        builder.Property(x => x.ProviderKind).HasColumnName("provider_kind").IsRequired();
        builder.Property(x => x.TargetId).HasColumnName("target_id").IsRequired();
        builder.Property(x => x.TargetLabel).HasColumnName("target_label").IsRequired();
        builder.Property(x => x.DeliveryIdentityRevision).HasColumnName("delivery_identity_revision").IsRequired();
        builder.Property(x => x.ConfigRevision).HasColumnName("config_revision").IsRequired();
        builder.Property(x => x.RouteRevision).HasColumnName("route_revision").IsRequired();
        builder.Property(x => x.State).HasColumnName("state").IsRequired();
        builder.Property(x => x.AttemptCount).HasColumnName("attempt_count").IsRequired();
        builder.Property(x => x.CycleNumber).HasColumnName("cycle_number").IsRequired();
        builder.Property(x => x.DueUtc).HasColumnName("due_utc").IsRequired();
        builder.Property(x => x.ClaimOwner).HasColumnName("claim_owner");
        builder.Property(x => x.ClaimExpiresUtc).HasColumnName("claim_expires_utc");
        builder.Property(x => x.TransportStartedUtc).HasColumnName("transport_started_utc");
        builder.Property(x => x.AcceptedUtc).HasColumnName("accepted_utc");
        builder.Property(x => x.RemoteReference).HasColumnName("remote_reference");
        builder.Property(x => x.ErrorCode).HasColumnName("error_code");
        builder.Property(x => x.ErrorText).HasColumnName("error_text");
        builder.Property(x => x.SuppressedReason).HasColumnName("suppressed_reason");
        builder.Property(x => x.PayloadTitle).HasColumnName("payload_title").IsRequired();
        builder.Property(x => x.PayloadBody).HasColumnName("payload_body").IsRequired();
        builder.Property(x => x.PayloadLinkPath).HasColumnName("payload_link_path");
        builder.Property(x => x.MessageId).HasColumnName("message_id").IsRequired();
        builder.Property(x => x.CreatedUtc).HasColumnName("created_utc").IsRequired();
        builder.Property(x => x.UpdatedUtc).HasColumnName("updated_utc").IsRequired();
        builder.Property(x => x.DismissedUtc).HasColumnName("dismissed_utc");
        builder.Property(x => x.Revision).HasColumnName("revision").IsRequired().IsConcurrencyToken();

        builder.HasIndex(x => new { x.OccurrenceId, x.DestinationId, x.TargetId, x.DeliveryIdentityRevision })
            .IsUnique().HasDatabaseName("ux_notification_deliveries_identity");
        builder.HasIndex(x => new { x.State, x.ProviderKind, x.DueUtc }).HasDatabaseName("ix_notification_deliveries_due");

        builder.HasOne(x => x.Occurrence).WithMany()
            .HasForeignKey(x => x.OccurrenceId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.Destination).WithMany()
            .HasForeignKey(x => x.DestinationId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class EpgNotificationCoverageConfiguration : IEntityTypeConfiguration<EpgNotificationCoverage>
{
    public void Configure(EntityTypeBuilder<EpgNotificationCoverage> builder)
    {
        builder.ToTable("epg_notification_coverage");
        builder.HasKey(x => x.EpgNotificationCoverageId);
        builder.Property(x => x.EpgNotificationCoverageId).HasColumnName("epg_notification_coverage_id");
        builder.Property(x => x.EpgSourceId).HasColumnName("epg_source_id").IsRequired();
        builder.Property(x => x.XmltvChannelId).HasColumnName("xmltv_channel_id").IsRequired();
        builder.Property(x => x.IsRelevant).HasColumnName("is_relevant").IsRequired();
        builder.Property(x => x.RelevanceContext).HasColumnName("relevance_context");
        builder.Property(x => x.IntervalsEncoded).HasColumnName("intervals_encoded").IsRequired();
        builder.Property(x => x.IntervalCount).HasColumnName("interval_count").IsRequired();
        builder.Property(x => x.EvidenceRevision).HasColumnName("evidence_revision");
        builder.Property(x => x.UpdatedUtc).HasColumnName("updated_utc").IsRequired();

        builder.HasIndex(x => new { x.EpgSourceId, x.XmltvChannelId }).IsUnique()
            .HasDatabaseName("ux_epg_notification_coverage_channel");

        builder.HasOne<EpgSource>().WithMany()
            .HasForeignKey(x => x.EpgSourceId).OnDelete(DeleteBehavior.Cascade);
    }
}
