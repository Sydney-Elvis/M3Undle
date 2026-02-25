using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace M3Undle.Web.Data.Configurations;

public sealed class ProviderConfiguration : IEntityTypeConfiguration<Provider>
{
    public void Configure(EntityTypeBuilder<Provider> builder)
    {
        builder.ToTable("providers");

        builder.HasKey(x => x.ProviderId);
        builder.Property(x => x.ProviderId).HasColumnName("provider_id");
        builder.Property(x => x.Name).HasColumnName("name").IsRequired();
        builder.Property(x => x.Enabled).HasColumnName("enabled").IsRequired();
        builder.Property(x => x.IsActive).HasColumnName("is_active").IsRequired().HasDefaultValue(false);
        builder.Property(x => x.PlaylistUrl).HasColumnName("playlist_url").IsRequired();
        builder.Property(x => x.XmltvUrl).HasColumnName("xmltv_url");
        builder.Property(x => x.HeadersJson).HasColumnName("headers_json");
        builder.Property(x => x.UserAgent).HasColumnName("user_agent");
        builder.Property(x => x.TimeoutSeconds).HasColumnName("timeout_seconds").HasDefaultValue(20);
        builder.Property(x => x.CreatedUtc).HasColumnName("created_utc").IsRequired();
        builder.Property(x => x.UpdatedUtc).HasColumnName("updated_utc").IsRequired();
        builder.Property(x => x.ConfigSourcePath).HasColumnName("config_source_path");
        builder.Property(x => x.NeedsEnvVarSubstitution).HasColumnName("needs_env_var_substitution").IsRequired();

        builder.HasIndex(x => x.Name).IsUnique();
        builder.HasIndex(x => x.Enabled).HasDatabaseName("idx_providers_enabled");
        builder.HasIndex(x => x.IsActive)
            .HasDatabaseName("idx_providers_is_active")
            .HasFilter("is_active = 1")
            .IsUnique();
    }
}

