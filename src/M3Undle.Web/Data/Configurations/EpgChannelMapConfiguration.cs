using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace M3Undle.Web.Data.Configurations;

public sealed class EpgChannelMapConfiguration : IEntityTypeConfiguration<EpgChannelMap>
{
    public void Configure(EntityTypeBuilder<EpgChannelMap> builder)
    {
        builder.ToTable("epg_channel_map");

        builder.HasKey(x => x.EpgMapId);
        builder.Property(x => x.EpgMapId).HasColumnName("epg_map_id");
        builder.Property(x => x.ProfileId).HasColumnName("profile_id").IsRequired();
        builder.Property(x => x.ChannelId).HasColumnName("channel_id").IsRequired();
        builder.Property(x => x.XmltvChannelId).HasColumnName("xmltv_channel_id").IsRequired();
        builder.Property(x => x.Source).HasColumnName("source").IsRequired();
        builder.Property(x => x.CreatedUtc).HasColumnName("created_utc").IsRequired();
        builder.Property(x => x.UpdatedUtc).HasColumnName("updated_utc").IsRequired();

        builder.HasIndex(x => new { x.ProfileId, x.ChannelId }).IsUnique();
        builder.HasIndex(x => new { x.ProfileId, x.XmltvChannelId })
            .HasDatabaseName("idx_epg_map_profile")
            .IsUnique();

        builder.HasOne(x => x.Profile)
            .WithMany(x => x.EpgChannelMaps)
            .HasForeignKey(x => x.ProfileId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.Channel)
            .WithMany(x => x.EpgChannelMaps)
            .HasForeignKey(x => x.ChannelId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

