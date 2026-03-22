using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace M3Undle.Web.Data.Configurations;

public sealed class EpgChannelMappingConfiguration : IEntityTypeConfiguration<EpgChannelMapping>
{
    public void Configure(EntityTypeBuilder<EpgChannelMapping> builder)
    {
        builder.ToTable("epg_channel_mappings");

        builder.HasKey(x => x.EpgChannelMappingId);
        builder.Property(x => x.EpgChannelMappingId).HasColumnName("epg_channel_mapping_id");
        builder.Property(x => x.ProfileId).HasColumnName("profile_id").IsRequired();
        builder.Property(x => x.ProviderChannelId).HasColumnName("provider_channel_id").IsRequired();
        builder.Property(x => x.EpgSourceId).HasColumnName("epg_source_id").IsRequired();
        builder.Property(x => x.XmltvChannelId).HasColumnName("xmltv_channel_id").IsRequired();
        builder.Property(x => x.MappingMode).HasColumnName("mapping_mode").IsRequired().HasDefaultValue("auto_id");
        builder.Property(x => x.Confidence).HasColumnName("confidence").IsRequired().HasDefaultValue(1.0f);
        builder.Property(x => x.CreatedUtc).HasColumnName("created_utc").IsRequired();
        builder.Property(x => x.UpdatedUtc).HasColumnName("updated_utc").IsRequired();

        builder.HasIndex(x => new { x.ProfileId, x.ProviderChannelId, x.EpgSourceId })
            .IsUnique()
            .HasDatabaseName("idx_epg_channel_mappings_profile_channel_source");

        builder.HasOne(x => x.Profile)
            .WithMany()
            .HasForeignKey(x => x.ProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.ProviderChannel)
            .WithMany()
            .HasForeignKey(x => x.ProviderChannelId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.EpgSource)
            .WithMany(x => x.ChannelMappings)
            .HasForeignKey(x => x.EpgSourceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
