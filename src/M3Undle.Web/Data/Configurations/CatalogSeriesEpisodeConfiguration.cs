using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace M3Undle.Web.Data.Configurations;

public sealed class CatalogSeriesEpisodeConfiguration : IEntityTypeConfiguration<CatalogSeriesEpisode>
{
    public void Configure(EntityTypeBuilder<CatalogSeriesEpisode> builder)
    {
        builder.ToTable("catalog_series_episodes");

        builder.HasKey(x => x.CatalogSeriesEpisodeId);
        builder.Property(x => x.CatalogSeriesEpisodeId).HasColumnName("catalog_series_episode_id");
        builder.Property(x => x.ProviderId).HasColumnName("provider_id").IsRequired();
        builder.Property(x => x.ProviderGroupId).HasColumnName("provider_group_id").IsRequired();
        builder.Property(x => x.ProviderItemKey).HasColumnName("provider_item_key").IsRequired();
        builder.Property(x => x.EpisodeKey).HasColumnName("episode_key").IsRequired();
        builder.Property(x => x.Title).HasColumnName("title").IsRequired();
        builder.Property(x => x.StreamUrl).HasColumnName("stream_url").IsRequired();
        builder.Property(x => x.FirstSeenUtc).HasColumnName("first_seen_utc").IsRequired();
        builder.Property(x => x.LastSeenUtc).HasColumnName("last_seen_utc").IsRequired();
        builder.Property(x => x.Active).HasColumnName("active").IsRequired().HasDefaultValue(true);

        builder.HasIndex(x => new { x.ProviderGroupId, x.ProviderItemKey, x.EpisodeKey })
            .IsUnique()
            .HasDatabaseName("idx_catalog_series_episodes_group_item_episode_unique");
        builder.HasIndex(x => new { x.ProviderId, x.Active })
            .HasDatabaseName("idx_catalog_series_episodes_provider_active");

        builder.HasOne(x => x.Provider)
            .WithMany(x => x.CatalogSeriesEpisodes)
            .HasForeignKey(x => x.ProviderId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.ProviderGroup)
            .WithMany(x => x.CatalogSeriesEpisodes)
            .HasForeignKey(x => x.ProviderGroupId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
