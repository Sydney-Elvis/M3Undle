using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace M3Undle.Web.Data.Configurations;

public sealed class DownstreamIntegrationConfiguration : IEntityTypeConfiguration<DownstreamIntegration>
{
    public void Configure(EntityTypeBuilder<DownstreamIntegration> builder)
    {
        builder.ToTable("downstream_integrations");

        builder.HasKey(x => x.DownstreamIntegrationId);
        builder.Property(x => x.DownstreamIntegrationId).HasColumnName("downstream_integration_id");
        builder.Property(x => x.ProfileId).HasColumnName("profile_id");
        builder.Property(x => x.Name).HasColumnName("name").IsRequired();
        builder.Property(x => x.Kind).HasColumnName("kind").IsRequired();
        builder.Property(x => x.BaseUrl).HasColumnName("base_url").IsRequired();
        builder.Property(x => x.ApiKeyEncrypted).HasColumnName("api_key_encrypted");
        builder.Property(x => x.WebhookHeadersJson).HasColumnName("webhook_headers_json");
        builder.Property(x => x.TriggerOnLineupUpdate).HasColumnName("trigger_on_lineup_update").IsRequired().HasDefaultValue(true);
        builder.Property(x => x.TriggerOnGuideUpdate).HasColumnName("trigger_on_guide_update").IsRequired().HasDefaultValue(true);
        builder.Property(x => x.Enabled).HasColumnName("enabled").IsRequired().HasDefaultValue(true);
        builder.Property(x => x.LastNotifiedUtc).HasColumnName("last_notified_utc");
        builder.Property(x => x.LastNotifyError).HasColumnName("last_notify_error");
        builder.Property(x => x.CreatedUtc).HasColumnName("created_utc").IsRequired();
        builder.Property(x => x.UpdatedUtc).HasColumnName("updated_utc").IsRequired();

        builder.HasIndex(x => x.ProfileId).HasDatabaseName("idx_downstream_integrations_profile");

        builder.HasOne(x => x.Profile)
            .WithMany()
            .HasForeignKey(x => x.ProfileId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
