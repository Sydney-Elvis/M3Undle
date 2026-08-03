using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace M3Undle.Web.Data.Configurations;

public sealed class ProviderGroupConfiguration : IEntityTypeConfiguration<ProviderGroup>
{
    public void Configure(EntityTypeBuilder<ProviderGroup> builder)
    {
        builder.ToTable("provider_groups");

        builder.HasKey(x => x.ProviderGroupId);
        builder.Property(x => x.ProviderGroupId).HasColumnName("provider_group_id");
        builder.Property(x => x.ProviderId).HasColumnName("provider_id").IsRequired();
        builder.Property(x => x.RawName).HasColumnName("raw_name").IsRequired();
        builder.Property(x => x.NormalizedName).HasColumnName("normalized_name");
        builder.Property(x => x.FirstSeenUtc).HasColumnName("first_seen_utc").IsRequired();
        builder.Property(x => x.LastSeenUtc).HasColumnName("last_seen_utc").IsRequired();
        builder.Property(x => x.Active).HasColumnName("active").IsRequired();
        builder.Property(x => x.ChannelCount).HasColumnName("channel_count");
        builder.Property(x => x.ContentType).HasColumnName("content_type").IsRequired().HasDefaultValue("live");

        // Keyed by content type as well as name: providers routinely publish the same category
        // name (e.g. "Comedy", "Sport") as separate live, VOD and series categories. Collapsing
        // them into one row would make a live group's filter decision silently govern catalog
        // content of the same name.
        builder.HasIndex(x => new { x.ProviderId, x.RawName, x.ContentType }).IsUnique();
        builder.HasIndex(x => new { x.ProviderId, x.Active }).HasDatabaseName("idx_provider_groups_provider_active");

        builder.HasOne(x => x.Provider)
            .WithMany(x => x.ProviderGroups)
            .HasForeignKey(x => x.ProviderId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

