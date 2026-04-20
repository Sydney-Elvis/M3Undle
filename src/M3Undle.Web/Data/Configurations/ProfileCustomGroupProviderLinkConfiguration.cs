using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace M3Undle.Web.Data.Configurations;

public sealed class ProfileCustomGroupProviderLinkConfiguration : IEntityTypeConfiguration<ProfileCustomGroupProviderLink>
{
    public void Configure(EntityTypeBuilder<ProfileCustomGroupProviderLink> builder)
    {
        builder.ToTable("profile_custom_group_provider_links");

        builder.HasKey(x => x.LinkId);
        builder.Property(x => x.LinkId).HasColumnName("link_id");
        builder.Property(x => x.CustomGroupId).HasColumnName("custom_group_id").IsRequired();
        builder.Property(x => x.ProviderGroupId).HasColumnName("provider_group_id").IsRequired();
        builder.Property(x => x.CreatedUtc).HasColumnName("created_utc").IsRequired();

        builder.HasIndex(x => new { x.CustomGroupId, x.ProviderGroupId })
            .IsUnique()
            .HasDatabaseName("idx_pcgpl_group_provider_unique");

        builder.HasOne(x => x.CustomGroup)
            .WithMany(x => x.ProviderLinks)
            .HasForeignKey(x => x.CustomGroupId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.ProviderGroup)
            .WithMany(x => x.CustomGroupProviderLinks)
            .HasForeignKey(x => x.ProviderGroupId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
