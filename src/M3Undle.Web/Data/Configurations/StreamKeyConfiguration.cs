using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace M3Undle.Web.Data.Configurations;

public sealed class StreamKeyConfiguration : IEntityTypeConfiguration<StreamKey>
{
    public void Configure(EntityTypeBuilder<StreamKey> builder)
    {
        builder.ToTable("stream_keys");

        builder.HasKey(x => x.Value);
        builder.Property(x => x.Value).HasColumnName("stream_key");
        builder.Property(x => x.ProfileId).HasColumnName("profile_id").IsRequired();
        builder.Property(x => x.ChannelId).HasColumnName("channel_id").IsRequired();
        builder.Property(x => x.CreatedUtc).HasColumnName("created_utc").IsRequired();
        builder.Property(x => x.LastUsedUtc).HasColumnName("last_used_utc");
        builder.Property(x => x.Revoked).HasColumnName("revoked").IsRequired();

        builder.HasIndex(x => new { x.ProfileId, x.ChannelId }).IsUnique();
        builder.HasIndex(x => new { x.ProfileId, x.Revoked }).HasDatabaseName("idx_stream_keys_profile");
        builder.HasIndex(x => x.ChannelId).HasDatabaseName("idx_stream_keys_channel");

        builder.HasOne(x => x.Profile)
            .WithMany(x => x.StreamKeys)
            .HasForeignKey(x => x.ProfileId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.Channel)
            .WithMany(x => x.StreamKeys)
            .HasForeignKey(x => x.ChannelId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

