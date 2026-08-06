using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace M3Undle.Web.Data.Configurations;

public sealed class CatalogItemConfiguration : IEntityTypeConfiguration<CatalogItem>
{
    public void Configure(EntityTypeBuilder<CatalogItem> builder)
    {
        builder.ToTable("catalog_items");

        builder.HasKey(x => x.CatalogItemId);
        builder.Property(x => x.CatalogItemId).HasColumnName("catalog_item_id");
        builder.Property(x => x.ProviderId).HasColumnName("provider_id").IsRequired();
        builder.Property(x => x.ProviderGroupId).HasColumnName("provider_group_id").IsRequired();
        builder.Property(x => x.ProviderItemKey).HasColumnName("provider_item_key").IsRequired();
        builder.Property(x => x.ContentType).HasColumnName("content_type").IsRequired();
        builder.Property(x => x.Title).HasColumnName("title").IsRequired();
        builder.Property(x => x.ArtworkUrl).HasColumnName("artwork_url");
        builder.Property(x => x.EpisodeCount).HasColumnName("episode_count").IsRequired().HasDefaultValue(0);
        builder.Property(x => x.FirstSeenUtc).HasColumnName("first_seen_utc").IsRequired();
        builder.Property(x => x.LastSeenUtc).HasColumnName("last_seen_utc").IsRequired();
        builder.Property(x => x.Active).HasColumnName("active").IsRequired().HasDefaultValue(true);

        builder.HasIndex(x => new { x.ProviderGroupId, x.ProviderItemKey })
            .IsUnique()
            .HasDatabaseName("idx_catalog_items_group_item_unique");
        builder.HasIndex(x => new { x.ProviderGroupId, x.Active, x.Title })
            .HasDatabaseName("idx_catalog_items_group_active_title");
        builder.HasIndex(x => new { x.ProviderId, x.ContentType, x.Active })
            .HasDatabaseName("idx_catalog_items_provider_type_active");

        builder.HasOne(x => x.Provider)
            .WithMany(x => x.CatalogItems)
            .HasForeignKey(x => x.ProviderId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.ProviderGroup)
            .WithMany(x => x.CatalogItems)
            .HasForeignKey(x => x.ProviderGroupId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
