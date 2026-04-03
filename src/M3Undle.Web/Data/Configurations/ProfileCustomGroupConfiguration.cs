using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace M3Undle.Web.Data.Configurations;

public sealed class ProfileCustomGroupConfiguration : IEntityTypeConfiguration<ProfileCustomGroup>
{
    public void Configure(EntityTypeBuilder<ProfileCustomGroup> builder)
    {
        builder.ToTable("profile_custom_groups");

        builder.HasKey(x => x.CustomGroupId);
        builder.Property(x => x.CustomGroupId).HasColumnName("custom_group_id");
        builder.Property(x => x.ProfileId).HasColumnName("profile_id").IsRequired();
        builder.Property(x => x.Name).HasColumnName("name").IsRequired();
        builder.Property(x => x.Decision).HasColumnName("decision").IsRequired().HasDefaultValue("include");
        builder.Property(x => x.ChannelMode).HasColumnName("channel_mode").IsRequired().HasDefaultValue("select");
        builder.Property(x => x.TrackingPolicy).HasColumnName("tracking_policy").IsRequired().HasDefaultValue("review");
        builder.Property(x => x.TrackingKeywords).HasColumnName("tracking_keywords");
        builder.Property(x => x.AutoNumStart).HasColumnName("auto_num_start");
        builder.Property(x => x.AutoNumEnd).HasColumnName("auto_num_end");
        builder.Property(x => x.TrackNewChannels).HasColumnName("track_new_channels").IsRequired().HasDefaultValue(false);
        builder.Property(x => x.SortOverride).HasColumnName("sort_override");
        builder.Property(x => x.CreatedUtc).HasColumnName("created_utc").IsRequired();
        builder.Property(x => x.UpdatedUtc).HasColumnName("updated_utc").IsRequired();

        builder.HasIndex(x => x.ProfileId)
            .HasDatabaseName("idx_pcg_profile_id");

        builder.HasIndex(x => new { x.ProfileId, x.Name })
            .IsUnique()
            .HasDatabaseName("idx_pcg_profile_name_unique");

        builder.HasOne(x => x.Profile)
            .WithMany(x => x.CustomGroups)
            .HasForeignKey(x => x.ProfileId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
