using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace M3Undle.Web.Data.Configurations;

public sealed class CanonicalChannelConfiguration : IEntityTypeConfiguration<CanonicalChannel>
{
    public void Configure(EntityTypeBuilder<CanonicalChannel> builder)
    {
        builder.ToTable("canonical_channels");

        builder.HasKey(x => x.ChannelId);
        builder.Property(x => x.ChannelId).HasColumnName("channel_id");
        builder.Property(x => x.ProfileId).HasColumnName("profile_id").IsRequired();
        builder.Property(x => x.DisplayName).HasColumnName("display_name").IsRequired();
        builder.Property(x => x.ChannelNumber).HasColumnName("channel_number").IsRequired();
        builder.Property(x => x.GroupName).HasColumnName("group_name");
        builder.Property(x => x.LogoUrl).HasColumnName("logo_url");
        builder.Property(x => x.Enabled).HasColumnName("enabled").IsRequired();
        builder.Property(x => x.IsEvent).HasColumnName("is_event").IsRequired();
        builder.Property(x => x.EventPolicy).HasColumnName("event_policy").IsRequired();
        builder.Property(x => x.Notes).HasColumnName("notes");
        builder.Property(x => x.CreatedUtc).HasColumnName("created_utc").IsRequired();
        builder.Property(x => x.UpdatedUtc).HasColumnName("updated_utc").IsRequired();

        builder.HasIndex(x => new { x.ProfileId, x.ChannelNumber })
            .HasDatabaseName("idx_canonical_channels_profile_number")
            .IsUnique();
        builder.HasIndex(x => new { x.ProfileId, x.Enabled }).HasDatabaseName("idx_canonical_channels_profile_enabled");

        builder.HasOne(x => x.Profile)
            .WithMany(x => x.CanonicalChannels)
            .HasForeignKey(x => x.ProfileId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

