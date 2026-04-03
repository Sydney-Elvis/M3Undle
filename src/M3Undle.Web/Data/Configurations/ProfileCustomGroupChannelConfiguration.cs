using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace M3Undle.Web.Data.Configurations;

public sealed class ProfileCustomGroupChannelConfiguration : IEntityTypeConfiguration<ProfileCustomGroupChannel>
{
    public void Configure(EntityTypeBuilder<ProfileCustomGroupChannel> builder)
    {
        builder.ToTable("profile_custom_group_channels");

        builder.HasKey(x => x.CustomGroupChannelId);
        builder.Property(x => x.CustomGroupChannelId).HasColumnName("custom_group_channel_id");
        builder.Property(x => x.CustomGroupId).HasColumnName("custom_group_id").IsRequired();
        builder.Property(x => x.ProviderChannelId).HasColumnName("provider_channel_id").IsRequired();
        builder.Property(x => x.State).HasColumnName("state").IsRequired().HasDefaultValue("included");
        builder.Property(x => x.ChannelNumber).HasColumnName("channel_number");
        builder.Property(x => x.DisplayNameOverride).HasColumnName("display_name_override");
        builder.Property(x => x.TvgIdOverride).HasColumnName("tvg_id_override");
        builder.Property(x => x.CreatedUtc).HasColumnName("created_utc").IsRequired();
        builder.Property(x => x.UpdatedUtc).HasColumnName("updated_utc").IsRequired();

        builder.HasIndex(x => new { x.CustomGroupId, x.ProviderChannelId })
            .IsUnique()
            .HasDatabaseName("idx_pcgc_group_channel_unique");

        builder.HasIndex(x => new { x.CustomGroupId, x.State })
            .HasDatabaseName("idx_pcgc_group_state");

        builder.HasOne(x => x.CustomGroup)
            .WithMany(x => x.Channels)
            .HasForeignKey(x => x.CustomGroupId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.ProviderChannel)
            .WithMany(x => x.CustomGroupChannels)
            .HasForeignKey(x => x.ProviderChannelId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
