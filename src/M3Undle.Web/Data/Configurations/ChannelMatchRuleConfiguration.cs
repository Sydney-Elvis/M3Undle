using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace M3Undle.Web.Data.Configurations;

public sealed class ChannelMatchRuleConfiguration : IEntityTypeConfiguration<ChannelMatchRule>
{
    public void Configure(EntityTypeBuilder<ChannelMatchRule> builder)
    {
        builder.ToTable("channel_match_rules");

        builder.HasKey(x => x.RuleId);
        builder.Property(x => x.RuleId).HasColumnName("rule_id");
        builder.Property(x => x.ProfileId).HasColumnName("profile_id").IsRequired();
        builder.Property(x => x.Enabled).HasColumnName("enabled").IsRequired();
        builder.Property(x => x.MatchType).HasColumnName("match_type").IsRequired();
        builder.Property(x => x.MatchValue).HasColumnName("match_value").IsRequired();
        builder.Property(x => x.TargetChannelId).HasColumnName("target_channel_id");
        builder.Property(x => x.TargetGroupName).HasColumnName("target_group_name");
        builder.Property(x => x.DefaultPriority).HasColumnName("default_priority").HasDefaultValue(1);
        builder.Property(x => x.IsEventRule).HasColumnName("is_event_rule").IsRequired();
        builder.Property(x => x.CreatedUtc).HasColumnName("created_utc").IsRequired();
        builder.Property(x => x.UpdatedUtc).HasColumnName("updated_utc").IsRequired();

        builder.HasIndex(x => new { x.ProfileId, x.Enabled }).HasDatabaseName("idx_match_rules_profile");

        builder.HasOne(x => x.Profile)
            .WithMany(x => x.ChannelMatchRules)
            .HasForeignKey(x => x.ProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.TargetChannel)
            .WithMany(x => x.TargetingMatchRules)
            .HasForeignKey(x => x.TargetChannelId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

