using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace M3Undle.Web.Data.Configurations;

public sealed class ProfileCatalogGroupFilterConfiguration : IEntityTypeConfiguration<ProfileCatalogGroupFilter>
{
    public void Configure(EntityTypeBuilder<ProfileCatalogGroupFilter> builder)
    {
        builder.ToTable("profile_catalog_group_filters");

        builder.HasKey(x => x.ProfileCatalogGroupFilterId);
        builder.Property(x => x.ProfileCatalogGroupFilterId).HasColumnName("profile_catalog_group_filter_id");
        builder.Property(x => x.ProfileId).HasColumnName("profile_id").IsRequired();
        builder.Property(x => x.ProviderGroupId).HasColumnName("provider_group_id").IsRequired();
        builder.Property(x => x.Decision).HasColumnName("decision").IsRequired().HasDefaultValue("include");
        builder.Property(x => x.IsNew).HasColumnName("is_new").IsRequired().HasDefaultValue(false);
        builder.Property(x => x.CreatedUtc).HasColumnName("created_utc").IsRequired();
        builder.Property(x => x.UpdatedUtc).HasColumnName("updated_utc").IsRequired();

        builder.HasIndex(x => new { x.ProfileId, x.ProviderGroupId })
            .IsUnique()
            .HasDatabaseName("idx_pcgf_profile_group_unique");
        builder.HasIndex(x => new { x.ProfileId, x.Decision })
            .HasDatabaseName("idx_pcgf_profile_decision");

        builder.HasOne(x => x.Profile)
            .WithMany(x => x.ProfileCatalogGroupFilters)
            .HasForeignKey(x => x.ProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.ProviderGroup)
            .WithMany(x => x.ProfileCatalogGroupFilters)
            .HasForeignKey(x => x.ProviderGroupId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
