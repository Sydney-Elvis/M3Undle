using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace M3Undle.Web.Data.Configurations;

public sealed class EpgSourceChannelConfiguration : IEntityTypeConfiguration<EpgSourceChannel>
{
    public void Configure(EntityTypeBuilder<EpgSourceChannel> builder)
    {
        builder.ToTable("epg_source_channels");

        builder.HasKey(x => x.EpgSourceChannelId);
        builder.Property(x => x.EpgSourceChannelId).HasColumnName("epg_source_channel_id");
        builder.Property(x => x.EpgSourceId).HasColumnName("epg_source_id").IsRequired();
        builder.Property(x => x.XmltvChannelId).HasColumnName("xmltv_channel_id").IsRequired();
        builder.Property(x => x.DisplayName).HasColumnName("display_name").IsRequired();
        builder.Property(x => x.IconUrl).HasColumnName("icon_url");
        builder.Property(x => x.LastSeenUtc).HasColumnName("last_seen_utc").IsRequired();

        builder.HasIndex(x => new { x.EpgSourceId, x.XmltvChannelId })
            .IsUnique()
            .HasDatabaseName("idx_epg_source_channels_source_channel");

        builder.HasOne(x => x.EpgSource)
            .WithMany(x => x.Channels)
            .HasForeignKey(x => x.EpgSourceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
