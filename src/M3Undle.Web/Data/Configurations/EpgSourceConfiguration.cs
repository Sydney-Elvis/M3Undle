using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace M3Undle.Web.Data.Configurations;

public sealed class EpgSourceConfiguration : IEntityTypeConfiguration<EpgSource>
{
    public void Configure(EntityTypeBuilder<EpgSource> builder)
    {
        builder.ToTable("epg_sources");

        builder.HasKey(x => x.EpgSourceId);
        builder.Property(x => x.EpgSourceId).HasColumnName("epg_source_id");
        builder.Property(x => x.ProviderId).HasColumnName("provider_id");
        builder.Property(x => x.Name).HasColumnName("name").IsRequired();
        builder.Property(x => x.Kind).HasColumnName("kind").IsRequired().HasDefaultValue("xmltv_url");
        builder.Property(x => x.UrlOrPath).HasColumnName("url_or_path");
        builder.Property(x => x.Priority).HasColumnName("priority").IsRequired().HasDefaultValue(10);
        builder.Property(x => x.Enabled).HasColumnName("enabled").IsRequired().HasDefaultValue(true);
        builder.Property(x => x.HeadersJson).HasColumnName("headers_json");
        builder.Property(x => x.UserAgent).HasColumnName("user_agent");
        builder.Property(x => x.TimeoutSeconds).HasColumnName("timeout_seconds").IsRequired().HasDefaultValue(30);
        builder.Property(x => x.ETag).HasColumnName("etag");
        builder.Property(x => x.LastModifiedUtc).HasColumnName("last_modified_utc");
        builder.Property(x => x.LastSuccessUtc).HasColumnName("last_success_utc");
        builder.Property(x => x.LastFailureUtc).HasColumnName("last_failure_utc");
        builder.Property(x => x.RefreshIntervalHours).HasColumnName("refresh_interval_hours");
        builder.Property(x => x.CreatedUtc).HasColumnName("created_utc").IsRequired();
        builder.Property(x => x.UpdatedUtc).HasColumnName("updated_utc").IsRequired();

        builder.HasIndex(x => x.ProviderId).HasDatabaseName("idx_epg_sources_provider");
        builder.HasIndex(x => new { x.ProviderId, x.Priority }).HasDatabaseName("idx_epg_sources_provider_priority");

        builder.HasOne(x => x.Provider)
            .WithMany()
            .HasForeignKey(x => x.ProviderId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
