using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace M3Undle.Web.Data.Configurations;

public sealed class StreamChannelHealthEventConfiguration : IEntityTypeConfiguration<StreamChannelHealthEvent>
{
    public void Configure(EntityTypeBuilder<StreamChannelHealthEvent> builder)
    {
        builder.ToTable("stream_channel_health_events");
        builder.HasKey(e => e.StreamChannelHealthEventId);
        builder.Property(e => e.StreamChannelHealthEventId).HasColumnName("id");
        builder.Property(e => e.ProviderId).HasColumnName("provider_id").IsRequired();
        builder.Property(e => e.ProviderChannelId).HasColumnName("provider_channel_id").IsRequired();
        builder.Property(e => e.DisplayName).HasColumnName("display_name").IsRequired();
        builder.Property(e => e.EventKind).HasColumnName("event_kind").IsRequired();
        builder.Property(e => e.EventUtc).HasColumnName("event_utc");
        builder.Property(e => e.SessionId).HasColumnName("session_id");
        builder.Property(e => e.RelayMode).HasColumnName("relay_mode");
        builder.Property(e => e.RouteClassification).HasColumnName("route_classification");
        builder.Property(e => e.UpstreamFailureKind).HasColumnName("upstream_failure_kind");
        builder.Property(e => e.ReconnectAttempt).HasColumnName("reconnect_attempt");
        builder.Property(e => e.StallDurationMs).HasColumnName("stall_duration_ms");
        builder.Property(e => e.RecoveryDurationMs).HasColumnName("recovery_duration_ms");
        builder.Property(e => e.SafeStartWaitMs).HasColumnName("safe_start_wait_ms");
        builder.Property(e => e.OutputHeldMs).HasColumnName("output_held_ms");
        builder.Property(e => e.SafeStartKind).HasColumnName("safe_start_kind");
        builder.Property(e => e.ClientDisconnectReason).HasColumnName("client_disconnect_reason");
        builder.Property(e => e.ClientAbortAfterRecovery).HasColumnName("client_abort_after_recovery");
        builder.Property(e => e.ClientAbortAfterRecoveryDelayMs).HasColumnName("client_abort_after_recovery_delay_ms");
        builder.Property(e => e.ForcedRetune).HasColumnName("forced_retune");
        builder.Property(e => e.TsSyncLoss).HasColumnName("ts_sync_loss");
        builder.Property(e => e.BytesSuppressed).HasColumnName("bytes_suppressed");

        builder.HasIndex(e => new { e.ProviderId, e.ProviderChannelId, e.EventUtc })
            .HasDatabaseName("ix_stream_channel_health_events_provider_channel_event_utc");
        builder.HasIndex(e => new { e.EventKind, e.EventUtc })
            .HasDatabaseName("ix_stream_channel_health_events_event_kind_event_utc");
        builder.HasIndex(e => e.SessionId)
            .HasDatabaseName("ix_stream_channel_health_events_session_id");
    }
}
