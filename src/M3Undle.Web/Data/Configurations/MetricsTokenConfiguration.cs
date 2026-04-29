using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace M3Undle.Web.Data.Configurations;

public sealed class MetricsTokenConfiguration : IEntityTypeConfiguration<MetricsToken>
{
    public void Configure(EntityTypeBuilder<MetricsToken> builder)
    {
        builder.ToTable("metrics_tokens");

        builder.HasKey(x => x.MetricsTokenId);
        builder.Property(x => x.MetricsTokenId).HasColumnName("metrics_token_id");
        builder.Property(x => x.Name).HasColumnName("name").IsRequired();
        builder.Property(x => x.TokenHash).HasColumnName("token_hash").IsRequired();
        builder.Property(x => x.Scope).HasColumnName("scope").IsRequired().HasDefaultValue("metrics:read");
        builder.Property(x => x.CreatedUtc).HasColumnName("created_utc").IsRequired();
        builder.Property(x => x.LastUsedUtc).HasColumnName("last_used_utc");
        builder.Property(x => x.ExpiresUtc).HasColumnName("expires_utc");

        builder.HasIndex(x => x.Name).IsUnique();
    }
}
