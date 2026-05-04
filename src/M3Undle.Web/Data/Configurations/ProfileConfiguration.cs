using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace M3Undle.Web.Data.Configurations;

public sealed class ProfileConfiguration : IEntityTypeConfiguration<Profile>
{
    public void Configure(EntityTypeBuilder<Profile> builder)
    {
        builder.ToTable("profiles");

        builder.HasKey(x => x.ProfileId);
        builder.Property(x => x.ProfileId).HasColumnName("profile_id");
        builder.Property(x => x.Name).HasColumnName("name").IsRequired();
        builder.Property(x => x.Enabled).HasColumnName("enabled").IsRequired();
        builder.Property(x => x.IsActive).HasColumnName("is_active").IsRequired().HasDefaultValue(false);
        builder.Property(x => x.OutputName).HasColumnName("output_name").IsRequired();
        builder.Property(x => x.MergeMode).HasColumnName("merge_mode").IsRequired();
        builder.Property(x => x.RefreshScheduleKindOverride).HasColumnName("refresh_schedule_kind_override");
        builder.Property(x => x.RefreshStartupCatchupOverride).HasColumnName("refresh_startup_catchup_override");
        builder.Property(x => x.CreatedUtc).HasColumnName("created_utc").IsRequired();
        builder.Property(x => x.UpdatedUtc).HasColumnName("updated_utc").IsRequired();

        builder.HasIndex(x => x.Name).IsUnique();
        builder.HasIndex(x => x.IsActive)
            .HasDatabaseName("idx_profiles_is_active")
            .HasFilter("is_active = 1")
            .IsUnique();
    }
}
