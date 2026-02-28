using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace M3Undle.Web.Data.Configurations;

public sealed class ProfileGroupChannelFilterConfiguration : IEntityTypeConfiguration<ProfileGroupChannelFilter>
{
    public void Configure(EntityTypeBuilder<ProfileGroupChannelFilter> builder)
    {
        builder.ToTable("profile_group_channel_filters");

        builder.HasKey(x => x.ProfileGroupChannelFilterId);
        builder.Property(x => x.ProfileGroupChannelFilterId).HasColumnName("profile_group_channel_filter_id");
        builder.Property(x => x.ProfileGroupFilterId).HasColumnName("profile_group_filter_id").IsRequired();
        builder.Property(x => x.ProviderChannelId).HasColumnName("provider_channel_id").IsRequired();
        builder.Property(x => x.OutputGroupName).HasColumnName("output_group_name");
        builder.Property(x => x.ChannelNumber).HasColumnName("channel_number");
        builder.Property(x => x.CreatedUtc).HasColumnName("created_utc").IsRequired();

        builder.HasIndex(x => new { x.ProfileGroupFilterId, x.ProviderChannelId })
            .IsUnique()
            .HasDatabaseName("idx_pgcf_filter_channel_unique");

        builder.HasOne(x => x.ProfileGroupFilter)
            .WithMany(x => x.ChannelFilters)
            .HasForeignKey(x => x.ProfileGroupFilterId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.ProviderChannel)
            .WithMany(x => x.ChannelFilters)
            .HasForeignKey(x => x.ProviderChannelId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
