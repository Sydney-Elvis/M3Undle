using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace M3Undle.Web.Data.Configurations;

public sealed class ProfileGroupFilterConfiguration : IEntityTypeConfiguration<ProfileGroupFilter>
{
    public void Configure(EntityTypeBuilder<ProfileGroupFilter> builder)
    {
        builder.ToTable("profile_group_filters");

        builder.HasKey(x => x.ProfileGroupFilterId);
        builder.Property(x => x.ProfileGroupFilterId).HasColumnName("profile_group_filter_id");
        builder.Property(x => x.ProfileId).HasColumnName("profile_id").IsRequired();
        builder.Property(x => x.ProviderGroupId).HasColumnName("provider_group_id").IsRequired();
        builder.Property(x => x.Decision).HasColumnName("decision").IsRequired().HasDefaultValue("pending");
        builder.Property(x => x.ChannelMode).HasColumnName("channel_mode").IsRequired().HasDefaultValue("all");
        builder.Property(x => x.OutputName).HasColumnName("output_name");
        builder.Property(x => x.AutoNumStart).HasColumnName("auto_num_start");
        builder.Property(x => x.AutoNumEnd).HasColumnName("auto_num_end");
        builder.Property(x => x.TrackNewChannels).HasColumnName("track_new_channels").IsRequired().HasDefaultValue(false);
        builder.Property(x => x.SortOverride).HasColumnName("sort_override");
        builder.Property(x => x.CreatedUtc).HasColumnName("created_utc").IsRequired();
        builder.Property(x => x.UpdatedUtc).HasColumnName("updated_utc").IsRequired();

        builder.HasIndex(x => new { x.ProfileId, x.ProviderGroupId })
            .IsUnique()
            .HasDatabaseName("idx_pgf_profile_group_unique");

        builder.HasIndex(x => new { x.ProfileId, x.Decision })
            .HasDatabaseName("idx_pgf_profile_decision");

        builder.HasOne(x => x.Profile)
            .WithMany(x => x.ProfileGroupFilters)
            .HasForeignKey(x => x.ProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.ProviderGroup)
            .WithMany(x => x.ProfileGroupFilters)
            .HasForeignKey(x => x.ProviderGroupId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
