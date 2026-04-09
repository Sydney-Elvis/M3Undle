using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace M3Undle.Web.Data.Configurations;

public sealed class ProfileEventInterestRuleConfiguration : IEntityTypeConfiguration<ProfileEventInterestRule>
{
    public void Configure(EntityTypeBuilder<ProfileEventInterestRule> builder)
    {
        builder.ToTable("profile_event_interest_rules");

        builder.HasKey(x => x.RuleId);
        builder.Property(x => x.RuleId).HasColumnName("rule_id");
        builder.Property(x => x.ProfileId).HasColumnName("profile_id").IsRequired();
        builder.Property(x => x.ProviderId).HasColumnName("provider_id");
        builder.Property(x => x.ProviderGroupId).HasColumnName("provider_group_id");
        builder.Property(x => x.Enabled).HasColumnName("enabled").IsRequired().HasDefaultValue(true);
        builder.Property(x => x.MatchType).HasColumnName("match_type").IsRequired();
        builder.Property(x => x.MatchValue).HasColumnName("match_value").IsRequired();
        builder.Property(x => x.Action).HasColumnName("action").IsRequired();
        builder.Property(x => x.Priority).HasColumnName("priority").IsRequired().HasDefaultValue(100);
        builder.Property(x => x.CreatedUtc).HasColumnName("created_utc").IsRequired();
        builder.Property(x => x.UpdatedUtc).HasColumnName("updated_utc").IsRequired();

        builder.HasIndex(x => new { x.ProfileId, x.Enabled, x.Priority })
            .HasDatabaseName("idx_peir_profile_enabled_priority");

        builder.HasOne(x => x.Profile)
            .WithMany(x => x.EventInterestRules)
            .HasForeignKey(x => x.ProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Provider)
            .WithMany()
            .HasForeignKey(x => x.ProviderId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(x => x.ProviderGroup)
            .WithMany()
            .HasForeignKey(x => x.ProviderGroupId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
