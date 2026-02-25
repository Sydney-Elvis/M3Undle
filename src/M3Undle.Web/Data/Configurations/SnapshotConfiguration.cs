using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace M3Undle.Web.Data.Configurations;

public sealed class SnapshotConfiguration : IEntityTypeConfiguration<Snapshot>
{
    public void Configure(EntityTypeBuilder<Snapshot> builder)
    {
        builder.ToTable("snapshots");

        builder.HasKey(x => x.SnapshotId);
        builder.Property(x => x.SnapshotId).HasColumnName("snapshot_id");
        builder.Property(x => x.ProfileId).HasColumnName("profile_id").IsRequired();
        builder.Property(x => x.CreatedUtc).HasColumnName("created_utc").IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").IsRequired();
        builder.Property(x => x.PlaylistPath).HasColumnName("playlist_path").IsRequired();
        builder.Property(x => x.XmltvPath).HasColumnName("xmltv_path").IsRequired();
        builder.Property(x => x.ChannelIndexPath).HasColumnName("channel_index_path").IsRequired();
        builder.Property(x => x.StatusJsonPath).HasColumnName("status_json_path").IsRequired();
        builder.Property(x => x.ChannelCountPublished).HasColumnName("channel_count_published").IsRequired();
        builder.Property(x => x.ErrorSummary).HasColumnName("error_summary");

        builder.HasIndex(x => new { x.ProfileId, x.Status, x.CreatedUtc })
            .HasDatabaseName("idx_snapshots_profile_status")
            .IsDescending(false, false, true);

        builder.HasOne(x => x.Profile)
            .WithMany(x => x.Snapshots)
            .HasForeignKey(x => x.ProfileId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

