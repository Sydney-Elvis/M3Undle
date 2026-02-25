using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace M3Undle.Web.Data.Configurations;

public sealed class ProviderChannelConfiguration : IEntityTypeConfiguration<ProviderChannel>
{
    public void Configure(EntityTypeBuilder<ProviderChannel> builder)
    {
        builder.ToTable("provider_channels");

        builder.HasKey(x => x.ProviderChannelId);
        builder.Property(x => x.ProviderChannelId).HasColumnName("provider_channel_id");
        builder.Property(x => x.ProviderId).HasColumnName("provider_id").IsRequired();
        builder.Property(x => x.ProviderChannelKey).HasColumnName("provider_channel_key");
        builder.Property(x => x.DisplayName).HasColumnName("display_name").IsRequired();
        builder.Property(x => x.TvgId).HasColumnName("tvg_id");
        builder.Property(x => x.TvgName).HasColumnName("tvg_name");
        builder.Property(x => x.LogoUrl).HasColumnName("logo_url");
        builder.Property(x => x.StreamUrl).HasColumnName("stream_url").IsRequired();
        builder.Property(x => x.GroupTitle).HasColumnName("group_title");
        builder.Property(x => x.ProviderGroupId).HasColumnName("provider_group_id");
        builder.Property(x => x.IsEvent).HasColumnName("is_event").IsRequired();
        builder.Property(x => x.EventStartUtc).HasColumnName("event_start_utc");
        builder.Property(x => x.EventEndUtc).HasColumnName("event_end_utc");
        builder.Property(x => x.FirstSeenUtc).HasColumnName("first_seen_utc").IsRequired();
        builder.Property(x => x.LastSeenUtc).HasColumnName("last_seen_utc").IsRequired();
        builder.Property(x => x.Active).HasColumnName("active").IsRequired();
        builder.Property(x => x.LastFetchRunId).HasColumnName("last_fetch_run_id").IsRequired();

        builder.HasIndex(x => new { x.ProviderId, x.ProviderChannelKey })
            .IsUnique()
            .HasFilter("provider_channel_key IS NOT NULL");

        builder.HasIndex(x => new { x.ProviderId, x.Active }).HasDatabaseName("idx_provider_channels_provider_active");
        builder.HasIndex(x => new { x.ProviderId, x.LastSeenUtc })
            .HasDatabaseName("idx_provider_channels_seen")
            .IsDescending(false, true);
        builder.HasIndex(x => new { x.ProviderId, x.IsEvent, x.EventStartUtc })
            .HasDatabaseName("idx_provider_channels_is_event");

        builder.HasOne(x => x.Provider)
            .WithMany(x => x.ProviderChannels)
            .HasForeignKey(x => x.ProviderId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.ProviderGroup)
            .WithMany(x => x.ProviderChannels)
            .HasForeignKey(x => x.ProviderGroupId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(x => x.LastFetchRun)
            .WithMany(x => x.ProviderChannels)
            .HasForeignKey(x => x.LastFetchRunId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

