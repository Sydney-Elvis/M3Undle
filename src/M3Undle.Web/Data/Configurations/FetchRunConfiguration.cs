using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace M3Undle.Web.Data.Configurations;

public sealed class FetchRunConfiguration : IEntityTypeConfiguration<FetchRun>
{
    public void Configure(EntityTypeBuilder<FetchRun> builder)
    {
        builder.ToTable("fetch_runs");

        builder.HasKey(x => x.FetchRunId);
        builder.Property(x => x.FetchRunId).HasColumnName("fetch_run_id");
        builder.Property(x => x.ProviderId).HasColumnName("provider_id").IsRequired();
        builder.Property(x => x.StartedUtc).HasColumnName("started_utc").IsRequired();
        builder.Property(x => x.FinishedUtc).HasColumnName("finished_utc");
        builder.Property(x => x.Status).HasColumnName("status").IsRequired();
        builder.Property(x => x.Type).HasColumnName("type").IsRequired().HasDefaultValue("snapshot");
        builder.Property(x => x.ErrorSummary).HasColumnName("error_summary");
        builder.Property(x => x.PlaylistEtag).HasColumnName("playlist_etag");
        builder.Property(x => x.PlaylistLastModified).HasColumnName("playlist_last_modified");
        builder.Property(x => x.XmltvEtag).HasColumnName("xmltv_etag");
        builder.Property(x => x.XmltvLastModified).HasColumnName("xmltv_last_modified");
        builder.Property(x => x.PlaylistBytes).HasColumnName("playlist_bytes");
        builder.Property(x => x.XmltvBytes).HasColumnName("xmltv_bytes");
        builder.Property(x => x.ChannelCountSeen).HasColumnName("channel_count_seen");

        builder.HasIndex(x => new { x.ProviderId, x.StartedUtc })
            .HasDatabaseName("idx_fetch_runs_provider_time")
            .IsDescending(false, true);

        builder.HasIndex(x => new { x.Status, x.StartedUtc })
            .HasDatabaseName("idx_fetch_runs_status")
            .IsDescending(false, true);

        builder.HasOne(x => x.Provider)
            .WithMany(x => x.FetchRuns)
            .HasForeignKey(x => x.ProviderId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

