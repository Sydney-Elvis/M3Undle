using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace M3Undle.Web.Data.Configurations;

public sealed class XtreamSeriesCacheConfiguration : IEntityTypeConfiguration<XtreamSeriesCache>
{
    public void Configure(EntityTypeBuilder<XtreamSeriesCache> builder)
    {
        builder.ToTable("xtream_series_cache");

        builder.HasKey(x => new { x.ProviderId, x.SeriesId });
        builder.Property(x => x.ProviderId).HasColumnName("provider_id");
        builder.Property(x => x.SeriesId).HasColumnName("series_id");
        builder.Property(x => x.LastModifiedEpoch).HasColumnName("last_modified_epoch");
        builder.Property(x => x.EpisodesJson).HasColumnName("episodes_json").IsRequired();

        builder.HasOne(x => x.Provider)
            .WithMany()
            .HasForeignKey(x => x.ProviderId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
