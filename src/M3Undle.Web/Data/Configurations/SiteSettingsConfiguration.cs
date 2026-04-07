using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace M3Undle.Web.Data.Configurations;

public sealed class SiteSettingsConfiguration : IEntityTypeConfiguration<SiteSettings>
{
    public void Configure(EntityTypeBuilder<SiteSettings> builder)
    {
        builder.ToTable("site_settings");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(s => s.AuthenticationEnabled)
            .HasColumnName("authentication_enabled")
            .HasDefaultValue(true);
        builder.Property(s => s.EndpointSecurityEnabled)
            .HasColumnName("endpoint_security_enabled")
            .HasDefaultValue(false);
        builder.Property(s => s.StreamingEnabled)
            .HasColumnName("streaming_enabled")
            .HasDefaultValue(true);
        builder.Property(s => s.StreamMaxConcurrentSessions)
            .HasColumnName("stream_max_concurrent_sessions")
            .HasDefaultValue(50);
        builder.Property(s => s.StreamIdleGraceSeconds)
            .HasColumnName("stream_idle_grace_seconds")
            .HasDefaultValue(15);
        builder.Property(s => s.StreamIdleGraceHardCapSeconds)
            .HasColumnName("stream_idle_grace_hard_cap_seconds")
            .HasDefaultValue(120);
        builder.Property(s => s.StreamBufferMaxBytesPerSession)
            .HasColumnName("stream_buffer_max_bytes_per_session")
            .HasDefaultValue(4 * 1024 * 1024);
        builder.Property(s => s.StreamBufferMaxBytesHardCap)
            .HasColumnName("stream_buffer_max_bytes_hard_cap")
            .HasDefaultValue(32 * 1024 * 1024);
        builder.Property(s => s.StreamBufferReadChunkSizeBytes)
            .HasColumnName("stream_buffer_read_chunk_size_bytes")
            .HasDefaultValue(32 * 1024);
        builder.Property(s => s.StreamReconnectReadStallTimeoutSeconds)
            .HasColumnName("stream_reconnect_read_stall_timeout_seconds")
            .HasDefaultValue(30);
        builder.Property(s => s.StreamReconnectOutageWindowSeconds)
            .HasColumnName("stream_reconnect_outage_window_seconds")
            .HasDefaultValue(75);
        builder.Property(s => s.StreamReconnectConnectTimeoutSeconds)
            .HasColumnName("stream_reconnect_connect_timeout_seconds")
            .HasDefaultValue(15);
        builder.Property(s => s.StreamingSettingsRestartRequired)
            .HasColumnName("streaming_settings_restart_required")
            .HasDefaultValue(false);
        builder.Property(s => s.HdhrEnabled)
            .HasColumnName("hdhr_enabled")
            .HasDefaultValue(true);
        builder.Property(s => s.HdhrTunerCountOverride)
            .HasColumnName("hdhr_tuner_count_override");
        builder.Property(s => s.HdhrAdvertisedBaseUrl)
            .HasColumnName("hdhr_advertised_base_url");
        builder.Property(s => s.HdhrDiscoveryEnabled)
            .HasColumnName("hdhr_discovery_enabled")
            .HasDefaultValue(true);
        builder.Property(s => s.HdhrSsdpEnabled)
            .HasColumnName("hdhr_ssdp_enabled")
            .HasDefaultValue(true);
        builder.Property(s => s.HdhrSiliconDustDiscoveryEnabled)
            .HasColumnName("hdhr_silicondust_discovery_enabled")
            .HasDefaultValue(true);
        builder.Property(s => s.HdhrSettingsRestartRequired)
            .HasColumnName("hdhr_settings_restart_required")
            .HasDefaultValue(false);
        builder.Property(s => s.GeneratedHlsEnabled)
            .HasColumnName("generated_hls_enabled")
            .HasDefaultValue(true);
        builder.Property(s => s.GeneratedHlsFfmpegPath)
            .HasColumnName("generated_hls_ffmpeg_path");
        builder.Property(s => s.GeneratedHlsSettingsRestartRequired)
            .HasColumnName("generated_hls_settings_restart_required")
            .HasDefaultValue(false);
        builder.Property(s => s.RefreshScheduleKind)
            .HasColumnName("refresh_schedule_kind")
            .HasDefaultValue("6h");
        builder.Property(s => s.RefreshStartupCatchup)
            .HasColumnName("refresh_startup_catchup")
            .HasDefaultValue(true);

        builder.HasData(new SiteSettings
        {
            Id = 1,
            AuthenticationEnabled = false,
            EndpointSecurityEnabled = false,
            StreamingEnabled = true,
            StreamMaxConcurrentSessions = 50,
            StreamIdleGraceSeconds = 15,
            StreamIdleGraceHardCapSeconds = 120,
            StreamBufferMaxBytesPerSession = 4 * 1024 * 1024,
            StreamBufferMaxBytesHardCap = 32 * 1024 * 1024,
            StreamBufferReadChunkSizeBytes = 32 * 1024,
            StreamReconnectReadStallTimeoutSeconds = 30,
            StreamReconnectOutageWindowSeconds = 75,
            StreamReconnectConnectTimeoutSeconds = 15,
            StreamingSettingsRestartRequired = false,
            HdhrEnabled = true,
            HdhrTunerCountOverride = null,
            HdhrAdvertisedBaseUrl = null,
            HdhrDiscoveryEnabled = true,
            HdhrSsdpEnabled = true,
            HdhrSiliconDustDiscoveryEnabled = true,
            HdhrSettingsRestartRequired = false,
            GeneratedHlsEnabled = true,
            GeneratedHlsFfmpegPath = null,
            GeneratedHlsSettingsRestartRequired = false,
            RefreshScheduleKind = "6h",
            RefreshStartupCatchup = true,
        });
    }
}
