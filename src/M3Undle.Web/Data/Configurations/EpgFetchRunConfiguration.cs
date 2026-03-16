using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace M3Undle.Web.Data.Configurations;

public sealed class EpgFetchRunConfiguration : IEntityTypeConfiguration<EpgFetchRun>
{
    public void Configure(EntityTypeBuilder<EpgFetchRun> builder)
    {
        builder.ToTable("epg_fetch_runs");

        builder.HasKey(x => x.EpgFetchRunId);
        builder.Property(x => x.EpgFetchRunId).HasColumnName("epg_fetch_run_id");
        builder.Property(x => x.EpgSourceId).HasColumnName("epg_source_id").IsRequired();
        builder.Property(x => x.StartedUtc).HasColumnName("started_utc").IsRequired();
        builder.Property(x => x.FinishedUtc).HasColumnName("finished_utc");
        builder.Property(x => x.Status).HasColumnName("status").IsRequired();
        builder.Property(x => x.Bytes).HasColumnName("bytes");
        builder.Property(x => x.ChannelCount).HasColumnName("channel_count");
        builder.Property(x => x.ProgrammeCount).HasColumnName("programme_count");
        builder.Property(x => x.ErrorSummary).HasColumnName("error_summary");

        builder.HasIndex(x => new { x.EpgSourceId, x.StartedUtc })
            .HasDatabaseName("idx_epg_fetch_runs_source_time")
            .IsDescending(false, true);

        builder.HasOne(x => x.EpgSource)
            .WithMany(x => x.FetchRuns)
            .HasForeignKey(x => x.EpgSourceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
