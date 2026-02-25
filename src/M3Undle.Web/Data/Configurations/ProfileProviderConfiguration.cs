using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace M3Undle.Web.Data.Configurations;

public sealed class ProfileProviderConfiguration : IEntityTypeConfiguration<ProfileProvider>
{
    public void Configure(EntityTypeBuilder<ProfileProvider> builder)
    {
        builder.ToTable("profile_providers");

        builder.HasKey(x => new { x.ProfileId, x.ProviderId });
        builder.Property(x => x.ProfileId).HasColumnName("profile_id");
        builder.Property(x => x.ProviderId).HasColumnName("provider_id");
        builder.Property(x => x.Priority).HasColumnName("priority").IsRequired();
        builder.Property(x => x.Enabled).HasColumnName("enabled").IsRequired();

        builder.HasIndex(x => new { x.ProfileId, x.Priority }).HasDatabaseName("idx_profile_providers_profile");

        builder.HasOne(x => x.Profile)
            .WithMany(x => x.ProfileProviders)
            .HasForeignKey(x => x.ProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Provider)
            .WithMany(x => x.ProfileProviders)
            .HasForeignKey(x => x.ProviderId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

