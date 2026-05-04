using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace M3Undle.Web.Data.Configurations;

public sealed class SystemEventConfiguration : IEntityTypeConfiguration<SystemEvent>
{
    public void Configure(EntityTypeBuilder<SystemEvent> builder)
    {
        builder.ToTable("system_events");
        builder.HasKey(e => e.SystemEventId);
        builder.Property(e => e.SystemEventId).HasColumnName("id");
        builder.Property(e => e.EventType).HasColumnName("event_type").IsRequired();
        builder.Property(e => e.Severity).HasColumnName("severity").IsRequired();
        builder.Property(e => e.Title).HasColumnName("title").IsRequired();
        builder.Property(e => e.Detail).HasColumnName("detail");
        builder.Property(e => e.ProviderId).HasColumnName("provider_id");
        builder.Property(e => e.IntegrationId).HasColumnName("integration_id");
        builder.Property(e => e.OccurredAt).HasColumnName("occurred_at");
        builder.Property(e => e.OccurrenceCount).HasColumnName("occurrence_count").HasDefaultValue(1);

        builder.HasIndex(e => e.OccurredAt).HasDatabaseName("ix_system_events_occurred_at");

        builder.HasIndex(e => new { e.EventType, e.ProviderId })
            .HasDatabaseName("ix_system_events_event_type_provider_id")
            .HasFilter("\"provider_id\" IS NOT NULL");

        builder.HasIndex(e => new { e.EventType, e.IntegrationId })
            .HasDatabaseName("ix_system_events_event_type_integration_id")
            .HasFilter("\"integration_id\" IS NOT NULL");
    }
}
