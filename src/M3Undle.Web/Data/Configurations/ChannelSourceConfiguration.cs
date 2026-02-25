using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace M3Undle.Web.Data.Configurations;

public sealed class ChannelSourceConfiguration : IEntityTypeConfiguration<ChannelSource>
{
    public void Configure(EntityTypeBuilder<ChannelSource> builder)
    {
        builder.ToTable("channel_sources");

        builder.HasKey(x => x.ChannelSourceId);
        builder.Property(x => x.ChannelSourceId).HasColumnName("channel_source_id");
        builder.Property(x => x.ChannelId).HasColumnName("channel_id").IsRequired();
        builder.Property(x => x.ProviderId).HasColumnName("provider_id").IsRequired();
        builder.Property(x => x.ProviderChannelId).HasColumnName("provider_channel_id").IsRequired();
        builder.Property(x => x.Priority).HasColumnName("priority").IsRequired();
        builder.Property(x => x.Enabled).HasColumnName("enabled").IsRequired();
        builder.Property(x => x.OverrideStreamUrl).HasColumnName("override_stream_url");
        builder.Property(x => x.LastSuccessUtc).HasColumnName("last_success_utc");
        builder.Property(x => x.LastFailureUtc).HasColumnName("last_failure_utc");
        builder.Property(x => x.FailureCountRolling).HasColumnName("failure_count_rolling").HasDefaultValue(0);
        builder.Property(x => x.HealthState).HasColumnName("health_state").IsRequired();
        builder.Property(x => x.CreatedUtc).HasColumnName("created_utc").IsRequired();
        builder.Property(x => x.UpdatedUtc).HasColumnName("updated_utc").IsRequired();

        builder.HasIndex(x => new { x.ChannelId, x.Priority })
            .HasDatabaseName("idx_channel_sources_channel")
            .IsUnique();
        builder.HasIndex(x => new { x.HealthState, x.LastFailureUtc })
            .HasDatabaseName("idx_channel_sources_health")
            .IsDescending(false, true);

        builder.HasOne(x => x.Channel)
            .WithMany(x => x.ChannelSources)
            .HasForeignKey(x => x.ChannelId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Provider)
            .WithMany(x => x.ChannelSources)
            .HasForeignKey(x => x.ProviderId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.ProviderChannel)
            .WithMany(x => x.ChannelSources)
            .HasForeignKey(x => x.ProviderChannelId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

